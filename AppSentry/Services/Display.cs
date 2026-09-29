using System.Globalization;
using AppSentry.Core.Detection;
using AppSentry.Models;

namespace AppSentry.Services;

/// <summary>Color role used by the views; each maps to a Fluent SystemFillColor brush.</summary>
public enum Tone
{
    Neutral,
    Accent,
    Success,
    Caution,
    Critical
}

/// <summary>Formatting shared by every page, the notifications and the tray menu.</summary>
public static class Display
{
    // Segoe Fluent Icons / Segoe MDL2 Assets code points.
    public static class Glyphs
    {
        public const string Installed = "";
        public const string Updated = "";
        public const string Removed = "";
        public const string Modified = "";
        public const string Failed = "";
        public const string Warning = "";
        public const string Info = "";
        public const string Shield = "";
        public const string App = "";
        public const string Store = "";
        public const string Service = "";
        public const string Driver = "";
        public const string Task = "";
        public const string Folder = "";
        public const string Package = "";
        public const string Document = "";
        public const string Activity = "";
        public const string AllApps = "";
        public const string Filter = "";
        public const string Settings = "";
        public const string Refresh = "";
        public const string Search = "";
        public const string Copy = "";
        public const string OpenFolder = "";
        public const string Delete = "";
        public const string Add = "";
        public const string Check = "";
        public const string Pause = "";
        public const string Save = "";
    }

    public static string LocalTime(DateTime utc) => ToLocal(utc).ToString("yyyy-MM-dd HH:mm:ss");

    public static string ShortTime(DateTime utc) => ToLocal(utc).ToString("HH:mm");

    public static DateTime ToLocal(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();

    /// <summary>"Today", "Yesterday", "Monday, September 22", or with the year when it isn't this year.</summary>
    public static string DayLabel(DateTime utc)
    {
        var day = ToLocal(utc).Date;
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year
            ? day.ToString("dddd, MMMM d", CultureInfo.CurrentCulture)
            : day.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
    }

    public static string Who(ChangeEvent ev) =>
        FirstNonEmpty(ev.ChangedBy, ev.App.InstalledBy, ev.App.InstalledFor);

    public static string Size(long? bytes)
    {
        if (bytes is not { } b || b <= 0) return "";
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F0} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
        return $"{b / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static long? SizeBytes(ChangeEvent ev) => ev.SizeBytes ?? SizeBytes(ev.App);

    public static long? SizeBytes(InstalledApp app) => app.EstimatedSizeKb is { } kb and > 0 ? kb * 1024 : null;

    public static string TypeLabel(ChangeType type) => type switch
    {
        ChangeType.Installed => "Installed",
        ChangeType.Updated => "Updated",
        ChangeType.Removed => "Removed",
        ChangeType.Modified => "Modified",
        ChangeType.Failed => "Failed",
        _ => type.ToString()
    };

    public static string NotificationTitle(ChangeType type) => type switch
    {
        ChangeType.Installed => "App installed",
        ChangeType.Updated => "App updated",
        ChangeType.Removed => "App removed",
        ChangeType.Modified => "Change detected",
        ChangeType.Failed => "Install failed",
        _ => "Change detected"
    };

    public static string Glyph(ChangeType type) => type switch
    {
        ChangeType.Installed => Glyphs.Installed,
        ChangeType.Updated => Glyphs.Updated,
        ChangeType.Removed => Glyphs.Removed,
        ChangeType.Modified => Glyphs.Modified,
        ChangeType.Failed => Glyphs.Failed,
        _ => Glyphs.Info
    };

    public static string SourceGlyph(DetectionSource source) => source switch
    {
        DetectionSource.Store => Glyphs.Store,
        DetectionSource.Service => Glyphs.Service,
        DetectionSource.Driver => Glyphs.Driver,
        DetectionSource.ScheduledTask => Glyphs.Task,
        DetectionSource.FileSystem => Glyphs.Folder,
        DetectionSource.PackageManager => Glyphs.Package,
        DetectionSource.EventLog => Glyphs.Document,
        _ => Glyphs.App
    };

    public static string SourceLabel(DetectionSource source) => source switch
    {
        DetectionSource.Registry => "Add/Remove Programs",
        DetectionSource.EventLog => "Windows Installer log",
        DetectionSource.FileSystem => "Folder",
        DetectionSource.Service => "Windows service",
        DetectionSource.ScheduledTask => "Scheduled task",
        DetectionSource.Store => "Microsoft Store",
        DetectionSource.Driver => "Driver",
        DetectionSource.PackageManager => "Scoop",
        DetectionSource.Firewall => "Windows Firewall",
        DetectionSource.Network => "Network",
        _ => source.ToString()
    };

    public static Tone ToneFor(ChangeType type, AttentionLevel attention) => attention switch
    {
        AttentionLevel.Critical => Tone.Critical,
        AttentionLevel.Warning => Tone.Caution,
        _ => type switch
        {
            ChangeType.Installed => Tone.Success,
            ChangeType.Updated => Tone.Accent,
            ChangeType.Removed or ChangeType.Failed => Tone.Critical,
            ChangeType.Modified => Tone.Caution,
            _ => Tone.Neutral
        }
    };

    /// <summary>Strip the "[Windows Service] " style prefixes the engine puts on non-app names.</summary>
    public static string CleanName(string name)
    {
        if (name.StartsWith('['))
        {
            var close = name.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0) return name[(close + 2)..];
        }
        return name;
    }

    /// <summary>One line under the name in lists: what happened, compactly.</summary>
    public static string Summary(ChangeEvent ev, Attention attention)
    {
        var who = Who(ev);
        var by = who.Length > 0 && ev.ChangeType != ChangeType.Removed ? $" · {who}" : "";
        if (attention.Level >= AttentionLevel.Warning) return attention.Reason + by;
        return ev.ChangeType switch
        {
            ChangeType.Updated when !string.IsNullOrEmpty(ev.PreviousVersion) => $"{ev.PreviousVersion} → {ev.App.Version}{by}",
            ChangeType.Installed => $"Installed{Version(ev.App.Version)}{by}",
            ChangeType.Removed => $"Removed{Version(ev.App.Version)}",
            ChangeType.Modified => Shorten(ev.Details, 80),
            ChangeType.Failed => Shorten(ev.Details, 80),
            _ => $"{TypeLabel(ev.ChangeType)}{Version(ev.App.Version)}{by}"
        };
    }

    /// <summary>The details pane's opening sentence.</summary>
    public static string Sentence(ChangeEvent ev)
    {
        var who = Who(ev);
        var by = ev.ChangedBy.Length > 0 ? $" by {ev.ChangedBy}" : "";
        var version = ev.App.Version.Length > 0 ? $" {ev.App.Version}" : "";
        return ev.ChangeType switch
        {
            ChangeType.Updated when !string.IsNullOrEmpty(ev.PreviousVersion) =>
                $"Updated from {ev.PreviousVersion} to {ev.App.Version}{by}.",
            ChangeType.Installed => $"Version{version} was installed{by}{(ev.ChangedBy.Length == 0 && who.Length > 0 ? $" for {who}" : "")}.",
            ChangeType.Removed => $"Version{version} was removed{by}.",
            ChangeType.Modified => ev.Details.Length > 0 ? ev.Details : "Its settings changed without a version change.",
            ChangeType.Failed => ev.Details.Length > 0 ? ev.Details : "Windows Installer reported a failure.",
            _ => TypeLabel(ev.ChangeType)
        };
    }

    public static string Shorten(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Version(string version) => version.Length > 0 ? $" {version}" : "";

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
