using AppSentry.Core.Sources;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Detection;

/// <summary>
/// Joins Windows Installer events to the registry changes they caused.
///
/// The event log is the only source that knows <i>who</i> ran an install (the registry just
/// says "machine-wide"). v1 threw the event away whenever the registry had already seen the
/// app; here the match enriches the registry event with the account and the exact time.
/// Events with no registry counterpart (hidden MSI components, failures, repairs) are
/// recorded on their own.
/// </summary>
public static class MsiCorrelator
{
    public static List<ChangeEvent> Apply(
        IReadOnlyList<ChangeEvent> events,
        IReadOnlyList<MsiEvent> msiEvents,
        IReadOnlyDictionary<string, InstalledApp> snapshot,
        DateTime nowUtc)
    {
        if (msiEvents.Count == 0) return events.ToList();

        var unused = new List<MsiEvent>(msiEvents);
        var result = new List<ChangeEvent>(events.Count + msiEvents.Count);

        foreach (var ev in events)
        {
            if (ev.Source != DetectionSource.Registry)
            {
                result.Add(ev);
                continue;
            }

            MsiKind[] wanted = ev.ChangeType switch
            {
                ChangeType.Installed => [MsiKind.Installed],
                ChangeType.Updated => [MsiKind.Installed, MsiKind.Patched, MsiKind.Reconfigured],
                ChangeType.Modified => [MsiKind.Reconfigured, MsiKind.Installed, MsiKind.Patched],
                ChangeType.Removed => [MsiKind.Removed],
                _ => []
            };

            var match = Take(unused, ev.App, wanted);

            // A major upgrade also removed the old product; that removal belongs to this event.
            if (ev.ChangeType == ChangeType.Updated && ev.PreviousApp != null)
                Take(unused, ev.PreviousApp, [MsiKind.Removed]);

            if (match == null)
            {
                result.Add(ev);
                continue;
            }

            var who = match.UserName;
            var note = $"MSI event {match.EventId}{(who.Length > 0 ? $" by {who}" : "")}";
            result.Add(ev with
            {
                ChangedBy = who.Length > 0 ? who : ev.ChangedBy,
                OccurredAt = match.TimeUtc,
                App = ev.ChangeType == ChangeType.Removed || who.Length == 0 ? ev.App : ev.App with { InstalledBy = who },
                Details = Append(ev.Details, match.Detail.Length > 0 ? $"{note}: {match.Detail}" : note)
            });
        }

        // ── Leftovers ────────────────────────────────────────────────────────
        var anyRegistryChange = result.Any(e => e.Source == DetectionSource.Registry);
        foreach (var m in unused)
        {
            var known = FindInSnapshot(snapshot, m);
            var (type, details) = m.Kind switch
            {
                MsiKind.Failed => (ChangeType.Failed, m.Detail.Length > 0 ? m.Detail : "Windows Installer reported a failure"),
                MsiKind.Removed => (ChangeType.Removed, "Windows Installer removal (not listed in Add/Remove Programs)"),
                MsiKind.Installed when known != null => (ChangeType.Modified, "Reinstalled or repaired by Windows Installer; no change in Add/Remove Programs"),
                MsiKind.Installed => (ChangeType.Installed, "Hidden Windows Installer product (not listed in Add/Remove Programs)"),
                MsiKind.Patched => (ChangeType.Modified, m.Detail),
                _ => (ChangeType.Modified, "Repaired or reconfigured by Windows Installer")
            };

            var app = known ?? new InstalledApp
            {
                KeyPath = $@"EVENTLOG\MSI\{(m.ProductCode.Length > 0 ? m.ProductCode : m.ProductName)}",
                Name = m.ProductName,
                Version = m.Version,
                Publisher = m.Manufacturer,
                InstallType = "MSI",
                ProductCode = m.ProductCode,
                InstallSource = $"Event Log (ID {m.EventId})"
            };

            result.Add(new ChangeEvent
            {
                App = m.Kind == MsiKind.Installed && m.UserName.Length > 0 ? app with { InstalledBy = m.UserName } : app,
                ChangeType = type,
                PreviousVersion = null,
                DetectedAt = nowUtc,
                OccurredAt = m.TimeUtc,
                ChangedBy = m.UserName,
                Source = DetectionSource.EventLog,
                Details = $"{details} [MSI event {m.EventId}]",
                // Hidden components usually ride along with a visible install (e.g. the two
                // runtime MSIs inside a VC++ redistributable): keep them, but don't pop up for them.
                Silent = anyRegistryChange && type is ChangeType.Installed or ChangeType.Removed
            });
        }

        return result;
    }

    private static MsiEvent? Take(List<MsiEvent> pool, InstalledApp app, MsiKind[] kinds)
    {
        if (kinds.Length == 0 || pool.Count == 0) return null;
        var code = app.ProductCode.Length > 0 ? app.ProductCode : GuidTail(app.KeyPath);
        var name = NameNormalizer.Normalize(app.Name);

        var match = pool
            .Where(m => kinds.Contains(m.Kind))
            .Select(m => (m, score:
                code.Length > 0 && m.ProductCode.Equals(code, StringComparison.OrdinalIgnoreCase) ? 2 :
                m.ProductName.Equals(app.Name, StringComparison.OrdinalIgnoreCase) ? 1 :
                NameNormalizer.Normalize(m.ProductName) == name ? 0 : -1))
            .Where(x => x.score >= 0)
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.m.TimeUtc)
            .Select(x => x.m)
            .FirstOrDefault();

        if (match != null) pool.Remove(match);
        return match;
    }

    private static InstalledApp? FindInSnapshot(IReadOnlyDictionary<string, InstalledApp> snapshot, MsiEvent m)
    {
        if (m.ProductCode.Length > 0)
        {
            var byCode = snapshot.Values.FirstOrDefault(a => a.ProductCode.Equals(m.ProductCode, StringComparison.OrdinalIgnoreCase));
            if (byCode != null) return byCode;
        }
        return snapshot.Values.FirstOrDefault(a => a.Name.Equals(m.ProductName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GuidTail(string keyPath)
    {
        var idx = keyPath.LastIndexOf('\\');
        var tail = idx >= 0 ? keyPath[(idx + 1)..] : keyPath;
        return Guid.TryParse(tail, out _) ? tail.ToUpperInvariant() : "";
    }

    private static string Append(string existing, string addition) =>
        string.IsNullOrEmpty(existing) ? addition : $"{existing}; {addition}";
}
