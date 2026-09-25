using AppSentry.Core.Detection;
using AppSentry.Core.Sources;
using AppSentry.Core.Storage;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Engine;

/// <summary>
/// The scan pipeline:
///   0. MSI events   — read first, so every install they mention is already visible to step 1
///   1. Inventory    — registry, Store, Scoop (scope-aware)
///   2. Diff         — installs/updates/removals/modifications + upgrade correlation
///   3. Labels       — package managers
///   4. MSI join     — who did it, and events the registry can't see
///   5. Folders      — settled new/removed folders not owned by any app
///   6. Services, drivers, scheduled tasks
///   7. Commit       — exclusions, then events + every source's new state in one transaction
/// </summary>
public sealed partial class MonitorEngine
{
    private static readonly TimeSpan WingetMaxAge = TimeSpan.FromMinutes(30);

    private readonly PackageManagerSource _packages = new();

    private Dictionary<string, InstalledApp> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownScopes = new(StringComparer.OrdinalIgnoreCase);
    private MsiBookmark? _msiBookmark;
    private FsBaseline? _fsBaseline;
    private ServiceBaseline? _serviceBaseline;
    private TaskBaseline? _taskBaseline;
    private readonly Dictionary<string, string> _persistedJson = [];
    private bool _firstScanDone;

    private void LoadSourceState()
    {
        var saved = Store.GetState<Dictionary<string, InstalledApp>>(StateKeys.Snapshot) ?? [];
        // Entries without a scope come from a pre-v2 snapshot; drop them and re-baseline.
        _snapshot = saved.Where(kv => !string.IsNullOrEmpty(kv.Value.Scope))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _knownScopes = _snapshot.Count == saved.Count
            ? new HashSet<string>(Store.GetState<List<string>>(StateKeys.KnownScopes) ?? [], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _msiBookmark = Store.GetState<MsiBookmark>(StateKeys.MsiBookmark);
        _fsBaseline = Store.GetState<FsBaseline>(StateKeys.FileSystem);
        _serviceBaseline = Store.GetState<ServiceBaseline>(StateKeys.Services);
        _taskBaseline = Store.GetState<TaskBaseline>(StateKeys.Tasks);
    }

    private List<InstalledApp> CurrentInventory() => _snapshot.Values.ToList();

    private void ScanOnce(string reason)
    {
        var started = DateTime.UtcNow;
        PublishStatus(s => s with { IsScanning = true, LastError = null });
        var health = new Dictionary<string, string>();

        // ── 0. MSI events ─────────────────────────────────────────────────────
        var (msiEvents, nextBookmark) = MsiEventSource.Read(_msiBookmark, health);

        // ── 1. Inventory ──────────────────────────────────────────────────────
        var inventory = new InventoryResult();
        RunSource("Registry", health, () => RegistrySource.Scan(Context, inventory));
        RunSource("Store", health, () => StoreSource.Scan(Context, inventory));
        RunSource("Scoop", health, () => PackageManagerSource.ScanScoop(Context, inventory));
        foreach (var (k, v) in inventory.Health) health[k] = v;

        // ── 2. Diff ───────────────────────────────────────────────────────────
        RunSource("Chocolatey", health, () => _packages.RefreshChocolatey(health));
        var wingetFresh = !_firstScanDone && RunSource("Winget", health, () => _packages.RefreshWinget(Context, WingetMaxAge, health));
        var labelled = Relabel(inventory.Apps.Values);
        foreach (var app in labelled) inventory.Add(app);

        var diff = ChangeDetector.Detect(_snapshot, _knownScopes, inventory, started);
        var events = diff.Events;
        var snapshot = diff.Snapshot;

        // ── 3. Labels for anything new ────────────────────────────────────────
        if (!wingetFresh && events.Any(e => Scopes.IsRegistry(e.App.Scope) && e.ChangeType is ChangeType.Installed or ChangeType.Updated) &&
            RunSource("Winget", health, () => _packages.RefreshWinget(Context, TimeSpan.FromMinutes(1), health)))
        {
            foreach (var app in Relabel(snapshot.Values)) snapshot[app.KeyPath] = app;
            events = events.Select(e => snapshot.TryGetValue(e.App.KeyPath, out var fresh) && e.ChangeType != ChangeType.Removed
                ? e with { App = e.App with { PackageManager = fresh.PackageManager } }
                : e).ToList();
        }

        // ── 4. MSI join ───────────────────────────────────────────────────────
        events = MsiCorrelator.Apply(events, msiEvents, snapshot, started);

        // ── 5. Folders ────────────────────────────────────────────────────────
        FsBaseline? nextFs = _fsBaseline;
        RunSource("Folders", health, () =>
        {
            var ownership = new FolderOwnership(_snapshot.Values.Concat(snapshot.Values).Concat(RecentlyChangedApps(started)));
            var (fsEvents, fsNext, followUp) = FileSystemSource.Scan(_fsBaseline, FileSystemSource.GetRoots(Context), ownership, started, health);
            events.AddRange(fsEvents);
            nextFs = fsNext;
            if (followUp is { } due) ScheduleFollowUp(due);
        });

        // ── 6. Services, drivers, tasks ───────────────────────────────────────
        ServiceBaseline? nextServices = _serviceBaseline;
        RunSource("Services", health, () =>
        {
            var (svcEvents, svcNext) = ServiceSource.Scan(_serviceBaseline, Context, started, health);
            events.AddRange(svcEvents);
            nextServices = svcNext;
        });
        TaskBaseline? nextTasks = _taskBaseline;
        RunSource("Tasks", health, () =>
        {
            var (taskEvents, taskNext) = TaskSource.Scan(_taskBaseline, Context, started, health);
            events.AddRange(taskEvents);
            nextTasks = taskNext;
        });

        // ── 7. Commit ─────────────────────────────────────────────────────────
        // Size is captured now, once; the UI never walks folders again.
        events = SizeCalculator.Fill(events, TimeSpan.FromSeconds(20));

        var state = new Dictionary<string, object?>();
        Stage(state, StateKeys.Snapshot, snapshot);
        Stage(state, StateKeys.KnownScopes, diff.KnownScopes.OrderBy(s => s).ToList());
        Stage(state, StateKeys.MsiBookmark, nextBookmark);
        Stage(state, StateKeys.FileSystem, nextFs);
        Stage(state, StateKeys.Services, nextServices);
        Stage(state, StateKeys.Tasks, nextTasks);

        CommitScan(events, state);

        // Only after the transaction succeeded does in-memory state move forward.
        foreach (var (key, value) in state) _persistedJson[key] = (string)value!;
        _snapshot = snapshot;
        _knownScopes = diff.KnownScopes;
        _msiBookmark = nextBookmark;
        _fsBaseline = nextFs;
        _serviceBaseline = nextServices;
        _taskBaseline = nextTasks;

        var firstBaseline = diff.BaselinedScopes.Count > 0 && diff.BaselinedScopes.Count == diff.KnownScopes.Count;
        if (diff.BaselinedScopes.Count > 0)
            EngineLog.Info($"Baselined scope(s): {string.Join(", ", diff.BaselinedScopes)}");
        _firstScanDone = true;
        RefreshTriggers();

        PublishStatus(s => s with
        {
            IsScanning = false,
            LastScanUtc = DateTime.UtcNow,
            LastScanSeconds = (DateTime.UtcNow - started).TotalSeconds,
            TrackedApps = snapshot.Count,
            Sources = health,
            Notice = firstBaseline ? "Baseline taken — changes are reported from now on" : s.Notice
        });
    }

    private List<InstalledApp> Relabel(IEnumerable<InstalledApp> apps) =>
        apps.Select(a => (a, label: _packages.LabelFor(a)))
            .Where(x => x.label != x.a.PackageManager)
            .Select(x => x.a with { PackageManager = x.label })
            .ToList();

    /// <summary>Apps removed or replaced in the last day, so their leftover folders aren't reported as drops.</summary>
    private IEnumerable<InstalledApp> RecentlyChangedApps(DateTime nowUtc)
    {
        try
        {
            return Store.LoadEventsSince(nowUtc.AddDays(-1))
                .Where(e => e.ChangeType is ChangeType.Removed or ChangeType.Updated or ChangeType.Modified)
                .SelectMany(e => e.PreviousApp == null ? new[] { e.App } : new[] { e.App, e.PreviousApp })
                .ToList();
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Could not load recent events for folder ownership: {ex.Message}");
            return [];
        }
    }

    /// <summary>Adds a state key to the commit only if its serialized value changed.</summary>
    private void Stage(Dictionary<string, object?> state, string key, object? value)
    {
        if (value == null) return;
        var json = AppJson.Serialize(value);
        if (_persistedJson.TryGetValue(key, out var previous) && previous == json) return;
        state[key] = json;
    }

    /// <summary>Runs one source; an exception marks it failed without aborting the scan.</summary>
    private static bool RunSource(string name, Dictionary<string, string> health, Func<bool> scan)
    {
        try
        {
            return scan();
        }
        catch (Exception ex)
        {
            health[name] = $"failed: {ex.Message}";
            EngineLog.Error($"{name} source failed", ex);
            return false;
        }
    }

    private static void RunSource(string name, Dictionary<string, string> health, Action scan) =>
        RunSource(name, health, () => { scan(); return true; });
}
