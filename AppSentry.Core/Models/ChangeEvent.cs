using System.Text.Json.Serialization;

namespace AppSentry.Models;

public enum ChangeType
{
    Installed,
    Updated,
    Removed,
    Modified,   // Same version, but identity/uninstall values changed (repackaged, re-registered, hijacked)
    Failed      // An install or removal that the event log says failed
}

/// <summary>
/// How the change was detected.
/// </summary>
public enum DetectionSource
{
    Registry,       // Uninstall keys (HKLM 64/32, per-user hives)
    EventLog,       // Windows Event Log (MsiInstaller)
    FileSystem,     // New/removed folder in Program Files or %LOCALAPPDATA%\Programs
    Service,        // Windows service added/removed/reconfigured
    ScheduledTask,  // Scheduled task added/removed/changed
    Store,          // Microsoft Store / MSIX package
    Driver,         // Kernel or file-system driver
    PackageManager  // Scoop (apps that never touch the registry)
}

/// <summary>
/// A detected change (install, update, removal, modification or failure) for one application.
/// All DateTime values are UTC.
/// </summary>
public sealed record ChangeEvent
{
    /// <summary>Database row id. 0 until the event has been persisted.</summary>
    public long Id { get; init; }

    /// <summary>The app as it is after the change (for removals: as it was before).</summary>
    public InstalledApp App { get; init; } = new();

    public ChangeType ChangeType { get; init; }

    /// <summary>Only set for updates.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>When AppSentry noticed the change (UTC).</summary>
    public DateTime DetectedAt { get; init; }

    public DetectionSource Source { get; init; } = DetectionSource.Registry;

    /// <summary>Best-known time the change actually happened (UTC): event log time, key write time, etc.</summary>
    public DateTime? OccurredAt { get; init; }

    /// <summary>The app as it was before an update or modification. Drives Diff View.</summary>
    public InstalledApp? PreviousApp { get; init; }

    /// <summary>Account that performed the change, when known (MSI event log user).</summary>
    public string ChangedBy { get; init; } = "";

    /// <summary>Install size at detection time, in bytes.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Human-readable extra context: changed fields, event IDs, service config, etc.</summary>
    public string Details { get; init; } = "";

    /// <summary>True when an exclusion says "log, but don't notify".</summary>
    public bool Silent { get; init; }

    [JsonIgnore]
    public DateTime EffectiveTime => OccurredAt ?? DetectedAt;
}
