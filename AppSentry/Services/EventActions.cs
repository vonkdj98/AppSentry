using System.Diagnostics;
using System.IO;
using System.Text;
using AppSentry.Core;
using AppSentry.Core.Detection;
using AppSentry.Models;

namespace AppSentry.Services;

public enum DiffKind { Changed, Added, Removed, Same }

public sealed record DiffRow(string Name, string OldValue, string NewValue, DiffKind Kind);

/// <summary>Diffs, exports and actions for events and apps (shared by the pages and notifications).</summary>
public static class EventActions
{
    // ── Diff ──────────────────────────────────────────────────────────────────

    /// <summary>Before vs after values, both captured at detection time. Differences first.</summary>
    public static List<DiffRow> Diff(ChangeEvent ev)
    {
        var rows = new List<DiffRow>();
        var before = ev.PreviousApp;
        // No previous record (v1 events), or a slim copy still waiting for its full values.
        if (before == null || (before.RawValues == null && ev.App.RawValues == null))
        {
            if (!string.IsNullOrEmpty(ev.PreviousVersion))
                rows.Add(new DiffRow("DisplayVersion", ev.PreviousVersion, ev.App.Version, DiffKind.Changed));
            return rows;
        }

        if (!before.KeyPath.Equals(ev.App.KeyPath, StringComparison.OrdinalIgnoreCase))
            rows.Add(new DiffRow("Registry key", before.KeyPath, ev.App.KeyPath, DiffKind.Changed));

        var oldValues = before.RawValues ?? [];
        var newValues = ev.App.RawValues ?? [];
        var same = new List<DiffRow>();
        foreach (var key in oldValues.Keys.Union(newValues.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var o = oldValues.GetValueOrDefault(key) ?? "";
            var n = newValues.GetValueOrDefault(key) ?? "";
            if (o.Length == 0 && n.Length == 0) continue;
            var kind = o.Length == 0 ? DiffKind.Added : n.Length == 0 ? DiffKind.Removed
                : o.Equals(n, StringComparison.Ordinal) ? DiffKind.Same : DiffKind.Changed;
            (kind == DiffKind.Same ? same : rows).Add(new DiffRow(key, o, n, kind));
        }
        rows.AddRange(same);
        return rows;
    }

    // ── Text for the clipboard ───────────────────────────────────────────────

    public static string DetailsText(ChangeEvent ev)
    {
        var sb = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"{label,-17} {value}");
        }
        Line("App:", ev.App.Name);
        Line("Change:", Display.TypeLabel(ev.ChangeType));
        Line("Version:", ev.App.Version);
        Line("Previous version:", ev.PreviousVersion);
        Line("Publisher:", ev.App.Publisher);
        Line("Changed by:", ev.ChangedBy);
        Line("Installed for:", ev.App.InstalledFor);
        Line("Happened:", ev.OccurredAt is { } at ? Display.LocalTime(at) : null);
        Line("Detected:", Display.LocalTime(ev.DetectedAt));
        Line("Source:", Display.SourceLabel(ev.Source));
        Line("Install type:", ev.App.InstallType);
        Line("Location:", ev.App.InstallLocation);
        Line("Package manager:", ev.App.PackageManager);
        Line("Key:", ev.App.KeyPath);
        Line("Details:", ev.Details);
        return sb.ToString();
    }

    /// <summary>A bulleted write-up ready to paste into a change ticket.</summary>
    /// <param name="group">For a row that folds several profiles' copies of the change: all of them, so the record names every user.</param>
    public static string ChangeRecordText(ChangeEvent ev, IReadOnlyList<ChangeEvent>? group = null)
    {
        var attention = AttentionClassifier.Classify(ev);
        var version = ev.ChangeType == ChangeType.Updated && !string.IsNullOrEmpty(ev.PreviousVersion)
            ? $"{ev.PreviousVersion} → {ev.App.Version}"
            : ev.App.Version;
        var sb = new StringBuilder();
        sb.AppendLine($"Subject: {Display.TypeLabel(ev.ChangeType)} {Display.CleanName(ev.App.Name)} {version} on {Environment.MachineName}".TrimEnd());
        sb.AppendLine();
        void Bullet(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"- {label}: {value}");
        }
        Bullet("Change", Display.TypeLabel(ev.ChangeType));
        Bullet("Application", Display.CleanName(ev.App.Name));
        Bullet("Version", version);
        Bullet("Publisher", ev.App.Publisher);
        Bullet("Computer", Environment.MachineName);
        Bullet("Changed by", ev.ChangedBy.Length > 0 ? ev.ChangedBy : null);
        Bullet("Installed for", group is { Count: > 1 } ? UserGroups.InstalledFor(group) : ev.App.InstalledFor);
        Bullet("When", Display.LocalTime(ev.EffectiveTime));
        Bullet("Detected by", $"AppSentry ({Display.SourceLabel(ev.Source)})");
        if (attention.Level >= AttentionLevel.Warning) Bullet("Flagged", attention.Reason);
        Bullet("Details", ev.Details);
        return sb.ToString();
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    public static void ExportCsv(IEnumerable<ChangeEvent> events, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("OccurredAt,DetectedAt,Change,App,Version,PreviousVersion,Publisher,ChangedBy,InstalledFor,InstallType,Size,PackageManager,Source,Attention,Details,Key");
        foreach (var ev in events)
        {
            var attention = AttentionClassifier.Classify(ev);
            w.WriteLine(string.Join(",",
                Csv(ev.OccurredAt is { } at ? Display.LocalTime(at) : ""),
                Csv(Display.LocalTime(ev.DetectedAt)),
                Csv(ev.ChangeType.ToString()),
                Csv(ev.App.Name), Csv(ev.App.Version), Csv(ev.PreviousVersion ?? ""), Csv(ev.App.Publisher),
                Csv(ev.ChangedBy), Csv(ev.App.InstalledFor), Csv(ev.App.InstallType),
                Csv(Display.Size(Display.SizeBytes(ev))), Csv(ev.App.PackageManager), Csv(ev.Source.ToString()),
                Csv(attention.Level >= AttentionLevel.Warning ? attention.Reason : ""),
                Csv(ev.Details), Csv(ev.App.KeyPath)));
        }
    }

    private static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    // ── Uninstall ─────────────────────────────────────────────────────────────

    public static bool CanUninstall(InstalledApp app) =>
        app.PackageFullName.Length > 0 || app.UninstallString.Length > 0 || app.ProductCode.Length > 0;

    /// <summary>Confirms, then runs the uninstaller (or removes the Store package). Returns a status line.</summary>
    public static async Task<string?> UninstallAsync(InstalledApp app)
    {
        var plan = Uninstaller.Plan(app);
        if (plan == null)
        {
            Dialogs.ShowInfo("Uninstall", $"No uninstall command was found for {app.Name}. You can remove it from Settings > Apps.");
            return null;
        }

        var elevation = plan.Elevate ? "\n\nWindows will ask for administrator approval." : "";
        if (!Dialogs.Confirm("Uninstall", $"Uninstall {app.Name} {app.Version}?\n\n{plan.Display}{elevation}", destructive: true))
            return null;

        if (plan.IsStore)
        {
            var error = await Uninstaller.RemoveStorePackageAsync(plan.PackageFullName);
            return error == null ? $"Removed {app.Name}" : $"Couldn't remove {app.Name}: {error}";
        }

        try
        {
            Process.Start(new ProcessStartInfo(plan.FileName, plan.Arguments) { UseShellExecute = true, Verb = plan.Elevate ? "runas" : "" });
            return $"Started the uninstaller for {app.Name}. The change appears once it finishes.";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Uninstall cancelled";
        }
    }
}
