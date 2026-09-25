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
}
