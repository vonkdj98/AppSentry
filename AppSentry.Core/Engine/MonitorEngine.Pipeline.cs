using AppSentry.Core.Detection;
using AppSentry.Core.Sources;
using AppSentry.Core.Storage;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Engine;

// Scan pipeline. Inventory (registry + Store) and diffing are the v2 implementation;
// event log, folders, services/tasks and package-manager labels still use the v1
// scanners in Core/Legacy until phase 3.
public sealed partial class MonitorEngine
{
    private readonly PackageManagerDetector _legacyPkg = new();
    private readonly ServiceTaskScanner _legacySvc = new();
    private EventLogMonitor? _legacyEventLog;
    private FileSystemMonitor? _legacyFs;

    private Dictionary<string, InstalledApp> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownScopes = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastSnapshotJson;

    private void LoadSourceState()
    {
        var saved = Store.GetState<Dictionary<string, InstalledApp>>(StateKeys.Snapshot) ?? [];
        // Entries without a scope come from a pre-v2 snapshot; drop them and re-baseline.
        _snapshot = saved.Where(kv => !string.IsNullOrEmpty(kv.Value.Scope))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _knownScopes = _snapshot.Count == saved.Count
            ? new HashSet<string>(Store.GetState<List<string>>(StateKeys.KnownScopes) ?? [], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _legacyEventLog = new EventLogMonitor();
        _legacyFs = new FileSystemMonitor();
        _legacySvc.Initialize();
    }

    partial void OnStopping() => _legacyFs?.Dispose();

    private List<InstalledApp> CurrentInventory() => _snapshot.Values.ToList();

    private void ScanOnce(string reason)
    {
        var started = DateTime.UtcNow;
        PublishStatus(s => s with { IsScanning = true, LastError = null });

        // ── 1. Inventory ──────────────────────────────────────────────────────
        var inventory = new InventoryResult();
        RunSource("Registry", inventory.Health, () => RegistrySource.Scan(Context, inventory));
        RunSource("Store", inventory.Health, () => StoreSource.Scan(Context, inventory));

        _legacyPkg.Refresh();
        foreach (var app in inventory.Apps.Values.ToList())
        {
            var label = _legacyPkg.Detect(app.Name, app.Version, app.InstallLocation);
            if (label.Length > 0) inventory.Add(app with { PackageManager = label });
        }

        // ── 2. Diff ───────────────────────────────────────────────────────────
        var diff = ChangeDetector.Detect(_snapshot, _knownScopes, inventory, started);
        var events = diff.Events;

        // ── 3. Supplementary sources (v1) ────────────────────────────────────
        foreach (var evtLog in _legacyEventLog!.CheckForNewEvents())
        {
            if (events.Any(e => e.App.Name.Equals(evtLog.ProductName, StringComparison.OrdinalIgnoreCase)))
                continue;
            events.Add(new ChangeEvent
            {
                App = new InstalledApp
                {
                    KeyPath = $"EVENTLOG\\{evtLog.EventId}\\{evtLog.ProductName}",
                    Name = evtLog.ProductName,
                    Version = evtLog.Version,
                    InstalledBy = evtLog.UserName,
                    InstallSource = $"Event Log (ID {evtLog.EventId})",
                    InstallType = "MSI"
                },
                ChangeType = evtLog.ChangeType switch
                {
                    EventLogChangeType.Updated => ChangeType.Updated,
                    EventLogChangeType.Removed => ChangeType.Removed,
                    _ => ChangeType.Installed
                },
                DetectedAt = started,
                OccurredAt = evtLog.TimeGenerated.ToUniversalTime(),
                ChangedBy = evtLog.UserName,
                Source = DetectionSource.EventLog
            });
        }

        foreach (var fc in _legacyFs!.GetChangesAndResync())
        {
            if (diff.Snapshot.Values.Any(a =>
                    a.InstallLocation.Contains(fc.FolderName, StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Equals(fc.FolderName, StringComparison.OrdinalIgnoreCase)))
                continue;
            events.Add(new ChangeEvent
            {
                App = new InstalledApp
                {
                    KeyPath = $"FILESYSTEM\\{fc.FolderPath}",
                    Name = fc.FolderName,
                    InstallLocation = fc.FolderPath,
                    InstallSource = $"File drop in {Path.GetDirectoryName(fc.FolderPath)}",
                    InstallType = "Portable/Unknown"
                },
                ChangeType = fc.ChangeType == FolderChangeType.Created ? ChangeType.Installed : ChangeType.Removed,
                DetectedAt = started,
                Source = DetectionSource.FileSystem
            });
        }

        foreach (var sc in _legacySvc.CheckForChanges())
        {
            var typeLabel = sc.ItemType == ServiceTaskType.Service ? "Windows Service" : "Scheduled Task";
            events.Add(new ChangeEvent
            {
                App = new InstalledApp
                {
                    KeyPath = $"{sc.ItemType.ToString().ToUpperInvariant()}\\{sc.Name}",
                    Name = $"[{typeLabel}] {sc.Name}",
                    InstallSource = sc.Details,
                    InstallType = typeLabel
                },
                ChangeType = sc.ChangeType == ServiceTaskChangeType.Added ? ChangeType.Installed : ChangeType.Removed,
                DetectedAt = started,
                Source = sc.ItemType == ServiceTaskType.Service ? DetectionSource.Service : DetectionSource.ScheduledTask
            });
        }

        // ── 4. Persist ────────────────────────────────────────────────────────
        var state = new Dictionary<string, object?>();
        var snapshotJson = AppJson.Serialize(diff.Snapshot);
        if (snapshotJson != _lastSnapshotJson) state[StateKeys.Snapshot] = snapshotJson;
        if (!diff.KnownScopes.SetEquals(_knownScopes)) state[StateKeys.KnownScopes] = diff.KnownScopes.OrderBy(s => s).ToList();

        CommitScan(events, state);

        _snapshot = diff.Snapshot;
        _knownScopes = diff.KnownScopes;
        _lastSnapshotJson = snapshotJson;

        var firstBaseline = diff.BaselinedScopes.Count > 0 && diff.BaselinedScopes.Count == diff.KnownScopes.Count;
        if (diff.BaselinedScopes.Count > 0)
            EngineLog.Info($"Baselined scope(s): {string.Join(", ", diff.BaselinedScopes)}");

        PublishStatus(s => s with
        {
            IsScanning = false,
            LastScanUtc = DateTime.UtcNow,
            LastScanSeconds = (DateTime.UtcNow - started).TotalSeconds,
            TrackedApps = diff.Snapshot.Count,
            Sources = new Dictionary<string, string>(inventory.Health),
            Notice = firstBaseline ? "Baseline taken — changes are reported from now on" : s.Notice
        });
    }

    /// <summary>Runs one source; an exception marks it failed without aborting the scan.</summary>
    private static void RunSource(string name, Dictionary<string, string> health, Action scan)
    {
        try
        {
            scan();
        }
        catch (Exception ex)
        {
            health[name] = $"failed: {ex.Message}";
            EngineLog.Error($"{name} source failed", ex);
        }
    }
}
