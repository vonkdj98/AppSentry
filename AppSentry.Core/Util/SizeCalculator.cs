using AppSentry.Models;

namespace AppSentry.Core.Util;

/// <summary>
/// Install size, computed once when a change is detected and stored on the event.
/// v1 walked the whole install folder for every history row on every redraw and keystroke.
/// </summary>
public static class SizeCalculator
{
    private const int MaxFilesPerFolder = 200_000;
    private static readonly TimeSpan PerFolderBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fills <see cref="ChangeEvent.SizeBytes"/>: EstimatedSize from the uninstall key when the
    /// installer provided one, otherwise a capped walk of the install folder (installs and
    /// updates only — a removed app's folder is usually gone).
    /// </summary>
    public static List<ChangeEvent> Fill(List<ChangeEvent> events, TimeSpan totalBudget)
    {
        var deadline = DateTime.UtcNow + totalBudget;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var result = new List<ChangeEvent>(events.Count);

        foreach (var ev in events)
        {
            if (ev.SizeBytes != null)
            {
                result.Add(ev);
                continue;
            }
            if (ev.App.EstimatedSizeKb is { } kb and > 0)
            {
                result.Add(ev with { SizeBytes = kb * 1024 });
                continue;
            }

            var folder = ev.App.InstallLocation.Trim().Trim('"');
            var walk = ev.ChangeType is ChangeType.Installed or ChangeType.Updated
                       && folder.Length > 3
                       && DateTime.UtcNow < deadline
                       && !(windows.Length > 0 && folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                       && ev.Source is not (DetectionSource.Service or DetectionSource.Driver or DetectionSource.ScheduledTask);
            result.Add(walk && FolderSize(folder) is { } bytes ? ev with { SizeBytes = bytes } : ev);
        }
        return result;
    }

    public static long? FolderSize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return null;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint // don't follow junctions into loops or other volumes
            };
            var started = DateTime.UtcNow;
            long total = 0;
            var count = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                total += file.Length;
                if (++count >= MaxFilesPerFolder || DateTime.UtcNow - started > PerFolderBudget) break;
            }
            return total;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
