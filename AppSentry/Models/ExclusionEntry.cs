namespace AppSentry.Models;

/// <summary>
/// Represents an app excluded from notifications and/or logging.
/// </summary>
public record ExclusionEntry(
    string AppName,                // Display name to match (case-insensitive)
    bool ExcludeNotifications,     // Suppress popup/sound but still log
    bool ExcludeLogging            // Don't record at all (implies no notifications)
);
