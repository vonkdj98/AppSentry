using AppSentry.Models;

namespace AppSentry;

/// <summary>Formatting shared by the history list, details dialog, compare form and CSV export.</summary>
internal static class EventDisplay
{
    public static string LocalTime(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime())
        .ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Who made the change if known, otherwise who the app was installed for.</summary>
    public static string Who(ChangeEvent ev) =>
        FirstNonEmpty(ev.ChangedBy, ev.App.InstalledBy, ev.App.InstalledFor);

    public static string Size(ChangeEvent ev) =>
        ev.SizeBytes is { } bytes ? FormatSize(bytes) : Size(ev.App);

    public static string Size(InstalledApp app) =>
        app.EstimatedSizeKb is { } kb and > 0 ? FormatSize(kb * 1024) : "";

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // ── Per-change-type styling (one place for every form) ───────────────────

    public static Color RowBackground(ChangeType type, ThemeColors theme) => type switch
    {
        ChangeType.Installed => theme.InstalledBg,
        ChangeType.Updated => theme.UpdatedBg,
        ChangeType.Removed or ChangeType.Failed => theme.RemovedBg,
        ChangeType.Modified => theme.ModifiedBg,
        _ => theme.ListBg
    };

    public static Color Accent(ChangeType type, ThemeColors theme) => type switch
    {
        ChangeType.Installed => theme.InstalledAccent,
        ChangeType.Updated => theme.UpdatedAccent,
        ChangeType.Removed or ChangeType.Failed => theme.RemovedAccent,
        ChangeType.Modified => theme.ModifiedAccent,
        _ => theme.MutedFg
    };

    public static string Icon(ChangeType type) => type switch
    {
        ChangeType.Installed => "⬇",
        ChangeType.Updated => "⟳",
        ChangeType.Removed => "✕",
        ChangeType.Modified => "✎",
        ChangeType.Failed => "⚠",
        _ => "•"
    };

    public static string Title(ChangeType type) => type switch
    {
        ChangeType.Installed => "App Installed",
        ChangeType.Updated => "App Updated",
        ChangeType.Removed => "App Removed",
        ChangeType.Modified => "App Modified",
        ChangeType.Failed => "Install Failed",
        _ => "App Change Detected"
    };

    public static string Summary(ChangeEvent ev) => ev.ChangeType switch
    {
        ChangeType.Installed => $"Installed  ·  v{ev.App.Version}",
        ChangeType.Updated => $"Updated  ·  {ev.PreviousVersion} → {ev.App.Version}",
        ChangeType.Removed => $"Removed  ·  v{ev.App.Version}",
        ChangeType.Modified => $"Modified  ·  {Shorten(ev.Details, 60)}",
        ChangeType.Failed => $"Failed  ·  {Shorten(ev.Details, 60)}",
        _ => ev.ChangeType.ToString()
    };

    private static string Shorten(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";
}
