namespace AppTracker.Models;

public enum ChangeType
{
    Installed,
    Updated,
    Removed
}

/// <summary>
/// How the change was detected.
/// </summary>
public enum DetectionSource
{
    Registry,       // Standard registry scan
    EventLog,       // Windows Event Log (MsiInstaller)
    FileSystem,     // New folder in Program Files
    Service,        // New Windows Service registered
    ScheduledTask   // New Scheduled Task registered
}

/// <summary>
/// Represents a detected change (install, update, or removal) for an application.
/// </summary>
public record ChangeEvent(
    InstalledApp App,
    ChangeType ChangeType,
    string? PreviousVersion,   // Only set when ChangeType == Updated
    DateTime DetectedAt,
    DetectionSource Source = DetectionSource.Registry  // default for backward compat
);
