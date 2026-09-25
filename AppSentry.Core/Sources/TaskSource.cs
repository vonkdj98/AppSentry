using System.Runtime.InteropServices;
using AppSentry.Core.Engine;
using AppSentry.Models;

namespace AppSentry.Core.Sources;

public sealed record TaskRecord
{
    public string Path { get; init; } = "";
    public string Author { get; init; } = "";
    public string RunAs { get; init; } = "";
    public bool Highest { get; init; }
    public string Actions { get; init; } = "";
    public bool Enabled { get; init; }
}

public sealed class TaskBaseline
{
    public string Fingerprint { get; set; } = "";
    public Dictionary<string, TaskRecord> Tasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Scheduled tasks via the Task Scheduler COM API (Schedule.Service), including hidden tasks.
/// Captures what v1's schtasks parsing never had: the command each task runs, its author and
/// the account it runs as — and reports a task whose action or account changes as Modified.
/// Built-in \Microsoft\ tasks and known self-updaters are filtered in both directions.
/// </summary>
public static class TaskSource
{
    private const int TaskEnumHidden = 1;
    private const int ActionExec = 0;
    private const int ActionComHandler = 5;
    private const int RunLevelHighest = 1;

    private static readonly string[] UpdaterFragments =
    [
        "microsoftedgeupdate", "googleupdate", "googleupdater", "onedrive", "user_feed_synchronization",
        "createexplorershellunelevatedtask"
    ];

    public static (List<ChangeEvent> Events, TaskBaseline Next) Scan(
        TaskBaseline? baseline, EngineContext context, DateTime nowUtc, Dictionary<string, string> health)
    {
        var tasks = new Dictionary<string, TaskRecord>(StringComparer.OrdinalIgnoreCase);
        var unreadable = 0;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler COM class not registered");
            dynamic service = Activator.CreateInstance(type)!;
            try
            {
                service.Connect();
                Walk((object)service.GetFolder("\\"), tasks, ref unreadable);
            }
            finally
            {
                Release(service);
            }
        }
        catch (Exception ex)
        {
            health["Tasks"] = $"failed: {ex.Message}";
            return ([], baseline ?? new TaskBaseline());
        }

        // Some tasks are unreadable without elevation; that set is stable, so diffs stay valid.
        health["Tasks"] = unreadable == 0 ? "ok" : $"ok ({unreadable} not readable without elevation)";
        var next = new TaskBaseline { Fingerprint = context.Fingerprint, Tasks = tasks };
        if (baseline == null || baseline.Fingerprint != context.Fingerprint)
            return ([], next);

        var events = new List<ChangeEvent>();
        foreach (var (path, task) in tasks)
        {
            if (!baseline.Tasks.TryGetValue(path, out var before))
            {
                events.Add(Event(task, ChangeType.Installed, nowUtc, "New scheduled task"));
                continue;
            }
            var changes = new List<string>();
            if (!before.Actions.Equals(task.Actions, StringComparison.OrdinalIgnoreCase))
                changes.Add($"Actions: \"{before.Actions}\" → \"{task.Actions}\"");
            if (!before.RunAs.Equals(task.RunAs, StringComparison.OrdinalIgnoreCase) || before.Highest != task.Highest)
                changes.Add($"Run as: {RunAsLabel(before)} → {RunAsLabel(task)}");
            if (changes.Count > 0)
                events.Add(Event(task, ChangeType.Modified, nowUtc, string.Join("; ", changes), before));
        }
        foreach (var (path, before) in baseline.Tasks)
        {
            if (!tasks.ContainsKey(path))
                events.Add(Event(before, ChangeType.Removed, nowUtc, "Scheduled task deleted"));
        }
        return (events, next);
    }

    private static void Walk(object folderObject, Dictionary<string, TaskRecord> tasks, ref int unreadable)
    {
        dynamic folder = folderObject;
        try
        {
            string folderPath = folder.Path;
            if (folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                dynamic collection = folder.GetTasks(TaskEnumHidden);
                int count = collection.Count;
                for (var i = 1; i <= count; i++)
                {
                    dynamic? task = null;
                    try
                    {
                        task = collection[i];
                        TaskRecord? record = Read((object)task);
                        if (record != null) tasks[record.Path] = record;
                    }
                    catch (Exception)
                    {
                        unreadable++;
                    }
                    finally
                    {
                        Release(task);
                    }
                }
                Release(collection);
            }
            catch (Exception)
            {
                unreadable++;
            }

            dynamic folders = folder.GetFolders(0);
            int folderCount = folders.Count;
            for (var i = 1; i <= folderCount; i++)
                Walk((object)folders[i], tasks, ref unreadable);
            Release(folders);
        }
        finally
        {
            Release(folder);
        }
    }

    private static TaskRecord? Read(object taskObject)
    {
        dynamic task = taskObject;
        string path = task.Path;
        var lower = path.ToLowerInvariant();
        if (lower.StartsWith(@"\microsoft\") || UpdaterFragments.Any(lower.Contains)) return null;

        dynamic definition = task.Definition;
        dynamic principal = definition.Principal;
        string runAs = (string?)principal.UserId ?? "";
        if (runAs.Length == 0) runAs = (string?)principal.GroupId ?? "";

        var actions = new List<string>();
        dynamic actionCollection = definition.Actions;
        int count = actionCollection.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic action = actionCollection[i];
            int type = action.Type;
            actions.Add(type switch
            {
                ActionExec => $"{(string?)action.Path} {(string?)action.Arguments}".Trim(),
                ActionComHandler => $"COM handler {(string?)action.ClassId}",
                _ => $"action type {type}"
            });
            Release(action);
        }

        var record = new TaskRecord
        {
            Path = path,
            Author = (string?)definition.RegistrationInfo.Author ?? "",
            RunAs = runAs,
            Highest = (int)principal.RunLevel == RunLevelHighest,
            Actions = string.Join(" ; ", actions),
            Enabled = (bool)task.Enabled
        };
        Release(actionCollection);
        Release(principal);
        Release(definition);
        return record;
    }

    private static ChangeEvent Event(TaskRecord task, ChangeType type, DateTime nowUtc, string details, TaskRecord? before = null)
    {
        static InstalledApp ToApp(TaskRecord t)
        {
            var exe = FolderOwnership.ExecutableOf(t.Actions.Split(" ; ")[0]);
            string? folder = null;
            try { folder = exe.Length > 0 && Path.IsPathFullyQualified(exe) ? Path.GetDirectoryName(exe) : null; } catch { }
            return new InstalledApp
            {
                KeyPath = $@"SCHEDULEDTASK\{t.Path}",
                Scope = "TASKS",
                Name = $"[Scheduled Task] {t.Path}",
                Publisher = t.Author,
                InstallLocation = folder ?? "",
                InstallSource = $"Runs: {(t.Actions.Length > 0 ? t.Actions : "(no actions)")} | As: {RunAsLabel(t)}",
                InstallType = "Scheduled Task",
                InstalledFor = t.RunAs,
                RawValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Path"] = t.Path,
                    ["Author"] = t.Author,
                    ["RunAs"] = RunAsLabel(t),
                    ["Actions"] = t.Actions,
                    ["Enabled"] = t.Enabled.ToString()
                }
            };
        }

        return new ChangeEvent
        {
            App = ToApp(task),
            PreviousApp = before == null ? null : ToApp(before),
            ChangeType = type,
            DetectedAt = nowUtc,
            Source = DetectionSource.ScheduledTask,
            Details = details
        };
    }

    private static string RunAsLabel(TaskRecord t) =>
        $"{(t.RunAs.Length > 0 ? t.RunAs : "(creator)")}{(t.Highest ? " (highest privileges)" : "")}";

    private static void Release(object? com)
    {
        try
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch
        {
            // Best effort.
        }
    }
}
