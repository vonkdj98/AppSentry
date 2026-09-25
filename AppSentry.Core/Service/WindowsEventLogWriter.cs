using System.Diagnostics;
using System.Text;
using AppSentry.Core.Detection;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Service;

/// <summary>
/// Mirrors every recorded change into the Windows Application log (source "AppSentry") so a
/// SIEM agent such as Wazuh can collect it from every machine without talking to AppSentry.
///
/// Event IDs: 1000 installed · 1001 updated · 1002 removed · 1003 modified · 1004 failed.
/// The message body is "Key: value" lines, which Wazuh's eventchannel decoder keeps intact.
/// </summary>
public static class WindowsEventLogWriter
{
    public const string SourceName = "AppSentry";
    private const string LogName = "Application";

    /// <summary>Registers the event source. Needs admin (the installer and the service both call it).</summary>
    public static void EnsureSource()
    {
        if (!EventLog.SourceExists(SourceName))
            EventLog.CreateEventSource(SourceName, LogName);
    }

    public static int EventIdFor(ChangeType type) => type switch
    {
        ChangeType.Installed => 1000,
        ChangeType.Updated => 1001,
        ChangeType.Removed => 1002,
        ChangeType.Modified => 1003,
        _ => 1004
    };

    public static void Write(IEnumerable<ChangeEvent> events)
    {
        foreach (var ev in events)
        {
            try
            {
                // Same rules as the UI's "Needs a look", so a SIEM can alert on level alone.
                var entryType = AttentionClassifier.Classify(ev).Level switch
                {
                    AttentionLevel.Critical => EventLogEntryType.Error,
                    AttentionLevel.Warning => EventLogEntryType.Warning,
                    _ => EventLogEntryType.Information
                };
                EventLog.WriteEntry(SourceName, Format(ev), entryType, EventIdFor(ev.ChangeType));
            }
            catch (Exception ex)
            {
                EngineLog.Error("Could not write to the Windows event log", ex);
                return; // don't retry per event if the source is broken
            }
        }
    }

    public static string Format(ChangeEvent ev)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AppSentry: {ev.ChangeType} {ev.App.Name}{(ev.App.Version.Length > 0 ? " " + ev.App.Version : "")}");
        sb.AppendLine();
        void Line(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"{key}: {value}");
        }
        Line("Change", ev.ChangeType.ToString());
        var attention = AttentionClassifier.Classify(ev);
        if (attention.Level >= AttentionLevel.Warning) Line("Attention", $"{attention.Level}: {attention.Reason}");
        Line("App", ev.App.Name);
        Line("Version", ev.App.Version);
        Line("PreviousVersion", ev.PreviousVersion);
        Line("Publisher", ev.App.Publisher);
        Line("ChangedBy", ev.ChangedBy);
        Line("InstalledFor", ev.App.InstalledFor);
        Line("InstallType", ev.App.InstallType);
        Line("InstallLocation", ev.App.InstallLocation);
        Line("PackageManager", ev.App.PackageManager);
        Line("Source", ev.Source.ToString());
        Line("Key", ev.App.KeyPath);
        Line("OccurredAt", ev.OccurredAt?.ToString("O"));
        Line("DetectedAt", ev.DetectedAt.ToString("O"));
        Line("Details", ev.Details);
        return sb.ToString();
    }
}
