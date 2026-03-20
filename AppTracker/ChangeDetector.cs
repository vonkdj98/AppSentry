using AppTracker.Models;

namespace AppTracker;

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
        var now = DateTime.Now;

        // Find new installs and updates
        foreach (var (key, currentApp) in current)
        {
            if (!previous.TryGetValue(key, out var prevApp))
            {
                // New key — app was installed
                events.Add(new ChangeEvent(currentApp, ChangeType.Installed, null, now));
            }
            else if (!string.IsNullOrEmpty(currentApp.Version)
                     && currentApp.Version != prevApp.Version)
            {
                // Same key, different version — app was updated
                events.Add(new ChangeEvent(currentApp, ChangeType.Updated, prevApp.Version, now));
            }
        }

        // Find removals
        foreach (var (key, prevApp) in previous)
        {
            if (!current.ContainsKey(key))
            {
                events.Add(new ChangeEvent(prevApp, ChangeType.Removed, null, now));
            }
        }

        return events;
    }
}
