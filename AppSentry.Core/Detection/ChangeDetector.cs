using AppSentry.Core.Sources;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Detection;

public sealed record DiffResult(
    List<ChangeEvent> Events,
    Dictionary<string, InstalledApp> Snapshot,
    HashSet<string> KnownScopes,
    List<string> BaselinedScopes);

/// <summary>
/// Diffs the previous inventory snapshot against the current scan.
///
///  1. Scope-aware: only scopes the sources fully enumerated this scan are compared. Anything
///     else is carried forward unchanged, so a failed Store call or a signed-out user never
///     looks like mass uninstalls. A scope seen for the first time is baselined silently.
///  2. Same key, new version → Updated. Same key and version but identity/uninstall values
///     changed → Modified.
///  3. Correlation: a Removed and an Installed in the same scan for the same product (name
///     without version/arch, compatible publisher, same user/machine) is one Updated event.
///     This covers MSI major upgrades and installers that write a new key per version.
/// </summary>
public static class ChangeDetector
{
    public static DiffResult Detect(
        IReadOnlyDictionary<string, InstalledApp> previous,
        IReadOnlySet<string> knownScopes,
        InventoryResult current,
        DateTime nowUtc)
    {
        var completed = current.CompletedScopes;
        var snapshot = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, app) in previous)
            if (!completed.Contains(app.Scope)) snapshot[key] = app;       // carried forward
        foreach (var (key, app) in current.Apps)
            if (completed.Contains(app.Scope)) snapshot[key] = app;

        var baselined = completed.Where(s => !knownScopes.Contains(s)).OrderBy(s => s).ToList();
        var nextKnown = new HashSet<string>(knownScopes, StringComparer.OrdinalIgnoreCase);
        nextKnown.UnionWith(completed);

        bool Compared(string scope) => completed.Contains(scope) && knownScopes.Contains(scope);

        var events = new List<ChangeEvent>();
        var installed = new List<InstalledApp>();
        var removed = new List<InstalledApp>();

        foreach (var (key, app) in current.Apps)
        {
            if (!Compared(app.Scope)) continue;
            if (!previous.TryGetValue(key, out var before))
            {
                installed.Add(app);
                continue;
            }
            var change = CompareSameKey(before, app, nowUtc);
            if (change != null) events.Add(change);
        }

        foreach (var (key, before) in previous)
        {
            if (Compared(before.Scope) && !current.Apps.ContainsKey(key))
                removed.Add(before);
        }

        Correlate(installed, removed, events, nowUtc);
        return new DiffResult(events, snapshot, nextKnown, baselined);
    }

    // ── Same key ──────────────────────────────────────────────────────────────

    private static ChangeEvent? CompareSameKey(InstalledApp before, InstalledApp after, DateTime nowUtc)
    {
        var rewritten = after.KeyLastWriteUtc is { } w && w != before.KeyLastWriteUtc ? w : (DateTime?)null;

        if (!string.IsNullOrEmpty(after.Version) && !string.Equals(after.Version, before.Version, StringComparison.Ordinal))
        {
            return new ChangeEvent
            {
                App = after,
                PreviousApp = before,
                PreviousVersion = before.Version,
                ChangeType = ChangeType.Updated,
                DetectedAt = nowUtc,
                OccurredAt = rewritten,
                Source = Scopes.SourceOf(after.Scope)
            };
        }

        var changes = SignificantChanges(before, after);
        if (changes.Count == 0) return null;

        return new ChangeEvent
        {
            App = after,
            PreviousApp = before,
            ChangeType = ChangeType.Modified,
            DetectedAt = nowUtc,
            OccurredAt = rewritten,
            Source = Scopes.SourceOf(after.Scope),
            Details = string.Join("; ", changes)
        };
    }

    /// <summary>
    /// Values whose change at the same version is worth reporting: renames, publisher changes
    /// and — most importantly — a different uninstaller or install location, which is what a
    /// repackaged or hijacked entry looks like. Volatile values (size, dates) are ignored.
    /// </summary>
    private static List<string> SignificantChanges(InstalledApp before, InstalledApp after)
    {
        var changes = new List<string>();
        if (!Scopes.IsRegistry(after.Scope)) return changes; // Store/Scoop: only version changes matter

        void Check(string label, string a, string b, bool isPath = false)
        {
            var same = isPath
                ? string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase)
                : string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);
            if (!same) changes.Add($"{label}: {Quote(a)} → {Quote(b)}");
        }

        Check("Name", before.Name, after.Name);
        Check("Publisher", before.Publisher, after.Publisher);
        Check("InstallLocation", before.InstallLocation, after.InstallLocation, isPath: true);
        Check("UninstallString", before.UninstallString, after.UninstallString, isPath: true);
        Check("QuietUninstallString", before.QuietUninstallString, after.QuietUninstallString, isPath: true);
        return changes;
    }

    // ── Correlation ───────────────────────────────────────────────────────────

    private static void Correlate(List<InstalledApp> installed, List<InstalledApp> removed, List<ChangeEvent> events, DateTime nowUtc)
    {
        var unmatched = new List<InstalledApp>(removed);

        foreach (var app in installed.OrderBy(a => a.KeyPath, StringComparer.OrdinalIgnoreCase))
        {
            var name = NameNormalizer.Normalize(app.Name);
            var group = Scopes.Group(app.Scope);

            var match = unmatched
                .Where(r => Scopes.Group(r.Scope) == group
                            && NameNormalizer.Normalize(r.Name) == name
                            && NameNormalizer.PublishersCompatible(r.Publisher, app.Publisher))
                .OrderBy(r => string.Equals(r.Scope, app.Scope, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();

            if (match == null)
            {
                events.Add(new ChangeEvent
                {
                    App = app,
                    ChangeType = ChangeType.Installed,
                    DetectedAt = nowUtc,
                    OccurredAt = app.KeyLastWriteUtc,
                    Source = Scopes.SourceOf(app.Scope)
                });
                continue;
            }

            unmatched.Remove(match);
            var moved = $"re-registered from {KeyTail(match)} to {KeyTail(app)}";
            var versionChanged = !string.IsNullOrEmpty(app.Version) && !string.Equals(app.Version, match.Version, StringComparison.Ordinal);
            events.Add(new ChangeEvent
            {
                App = app,
                PreviousApp = match,
                PreviousVersion = versionChanged ? match.Version : null,
                ChangeType = versionChanged ? ChangeType.Updated : ChangeType.Modified,
                DetectedAt = nowUtc,
                OccurredAt = app.KeyLastWriteUtc,
                Source = Scopes.SourceOf(app.Scope),
                Details = versionChanged ? $"Upgrade {moved}" : $"Same version {moved}"
            });
        }

        foreach (var app in unmatched)
        {
            events.Add(new ChangeEvent
            {
                App = app,
                ChangeType = ChangeType.Removed,
                DetectedAt = nowUtc,
                Source = Scopes.SourceOf(app.Scope)
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string KeyTail(InstalledApp app)
    {
        var idx = app.KeyPath.LastIndexOf('\\');
        var tail = idx >= 0 ? app.KeyPath[(idx + 1)..] : app.KeyPath;
        return app.Scope == Scopes.Machine32 ? $"{tail} (32-bit)" : tail;
    }

    internal static string NormalizePath(string path) =>
        path.Trim().Trim('"').TrimEnd('\\', '/').Replace('/', '\\');

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length > 120 ? $"\"{value[..117]}…\"" : $"\"{value}\"";
    }
}
