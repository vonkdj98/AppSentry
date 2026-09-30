using AppSentry.Models;

namespace AppSentry.Services;

/// <summary>
/// The same change to the same app for several user profiles, shown as one. A Store update reaches every profile that
/// has the app, and the service records each profile's copy, so one update would otherwise be one row per profile.
/// The history underneath stays per profile; only the lists and notifications fold them together.
/// </summary>
public static class UserGroups
{
    /// <summary>Profiles' copies are recorded by the same scan; a few minutes covers a scan that runs long.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>What must match for two events to be the same change; null for events that are never per profile.</summary>
    public static string? Key(ChangeEvent ev) =>
        ev.App.InstalledFor.Length == 0 || ev.Source is not (DetectionSource.Store or DetectionSource.Registry)
            ? null
            : string.Join("|", ev.Source, ev.ChangeType, ev.App.Name, ev.App.Publisher, ev.App.Version, ev.PreviousVersion ?? "", ev.App.PackageFamilyName);

    /// <summary>
    /// Events in groups (most of them alone). In a group each profile appears once, and the one for
    /// <paramref name="preferredUser"/> (the signed-in user) comes first, as the one shown; otherwise the first recorded.
    /// </summary>
    public static List<List<ChangeEvent>> Group(IEnumerable<ChangeEvent> events, string preferredUser)
    {
        var groups = new List<List<ChangeEvent>>();
        var open = new Dictionary<string, List<ChangeEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in events.OrderBy(e => e.DetectedAt).ThenBy(e => e.Id))
        {
            var key = Key(ev);
            if (key != null && open.TryGetValue(key, out var group) && ev.DetectedAt - group[0].DetectedAt <= Window &&
                !group.Any(g => g.App.InstalledFor.Equals(ev.App.InstalledFor, StringComparison.OrdinalIgnoreCase)))
            {
                group.Add(ev);
                continue;
            }
            var fresh = new List<ChangeEvent> { ev };
            groups.Add(fresh);
            if (key != null) open[key] = fresh;
        }
        foreach (var group in groups.Where(g => g.Count > 1))
        {
            var mine = group.FindIndex(e => e.App.InstalledFor.Equals(preferredUser, StringComparison.OrdinalIgnoreCase));
            if (mine > 0) (group[0], group[mine]) = (group[mine], group[0]);
        }
        return groups;
    }

    /// <summary>"3 users (alex, sam, kim)"; just "7 users" when the names wouldn't fit; "" for one.</summary>
    public static string Users(IReadOnlyList<ChangeEvent> group)
    {
        if (group.Count <= 1) return "";
        var names = group.Select(e => e.App.InstalledFor).Where(n => n.Length > 0).ToList();
        return group.Count <= 4 ? $"{group.Count} users ({string.Join(", ", names)})" : $"{group.Count} users";
    }
}
