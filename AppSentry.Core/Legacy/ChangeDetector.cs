using AppSentry.Models;

namespace AppSentry;

/// <summary>
/// Compares two registry snapshots and returns a list of detected changes.
/// </summary>
public static class ChangeDetector
{
    /// <summary>
    /// Diffs the previous snapshot against the current snapshot.
    /// Returns events for newly installed, updated, and removed apps.
    /// </summary>
    public static List<ChangeEvent> Detect(
        Dictionary<string, InstalledApp> previous,
        Dictionary<string, InstalledApp> current)
    {
        var events = new List<ChangeEvent>();
        var now = DateTime.UtcNow;

        // Find new installs and updates
        foreach (var (key, currentApp) in current)
        {
            if (!previous.TryGetValue(key, out var prevApp))
            {
                // New key — app was installed
                events.Add(new ChangeEvent { App = currentApp, ChangeType = ChangeType.Installed, DetectedAt = now });
            }
            else if (!string.IsNullOrEmpty(currentApp.Version)
                     && currentApp.Version != prevApp.Version)
            {
                // Same key, different version — app was updated
                events.Add(new ChangeEvent { App = currentApp, ChangeType = ChangeType.Updated, PreviousVersion = prevApp.Version, PreviousApp = prevApp, DetectedAt = now });
            }
        }

        // Find removals
        foreach (var (key, prevApp) in previous)
        {
            if (!current.ContainsKey(key))
            {
                events.Add(new ChangeEvent { App = prevApp, ChangeType = ChangeType.Removed, DetectedAt = now });
            }
        }

        return events;
    }
}
