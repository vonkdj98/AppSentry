using AppSentry.Core.Storage;
using AppSentry.Models;

namespace AppSentry.Core.Engine;

// Phase 1: the scan pipeline still drives the v1 scanners (moved to Core/Legacy) so this
// commit changes storage, scheduling and exclusions without changing detection yet.
public sealed partial class MonitorEngine
{
    private readonly PackageManagerDetector _legacyPkg = new();
    private readonly ServiceTaskScanner _legacySvc = new();
    private EventLogMonitor? _legacyEventLog;
    private FileSystemMonitor? _legacyFs;
    private Dictionary<string, InstalledApp>? _snapshot;

    private void LoadSourceState()
    {
        var saved = Store.GetState<Dictionary<string, InstalledApp>>(StateKeys.Snapshot);
        _snapshot = saved == null ? null : new Dictionary<string, InstalledApp>(saved, StringComparer.OrdinalIgnoreCase);
        _legacyEventLog = new EventLogMonitor();
        _legacyFs = new FileSystemMonitor();
        _legacySvc.Initialize();
    }

    partial void OnStopping() => _legacyFs?.Dispose();

    private List<InstalledApp> CurrentInventory() => _snapshot?.Values.ToList() ?? [];

    private void ScanOnce(string reason)
    {
        var started = DateTime.UtcNow;
        PublishStatus(s => s with { IsScanning = true, LastError = null });

        _legacyPkg.Refresh();
        var current = RegistryScanner.Scan()
            .ToDictionary(kv => kv.Key,
                kv => kv.Value with { PackageManager = _legacyPkg.Detect(kv.Value.Name, kv.Value.Version, kv.Value.InstallLocation) },
                StringComparer.OrdinalIgnoreCase);

        var baseline = _snapshot == null;
        List<ChangeEvent> events = baseline ? [] : ChangeDetector.Detect(_snapshot!, current);

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
            if (current.Values.Any(a =>
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
                    InstalledBy = "Unknown",
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
                    InstalledBy = "SYSTEM/Admin",
                    InstallSource = sc.Details,
                    InstallType = typeLabel
                },
                ChangeType = sc.ChangeType == ServiceTaskChangeType.Added ? ChangeType.Installed : ChangeType.Removed,
                DetectedAt = started,
                Source = sc.ItemType == ServiceTaskType.Service ? DetectionSource.Service : DetectionSource.ScheduledTask
            });
        }

        CommitScan(events, new Dictionary<string, object?> { [StateKeys.Snapshot] = current });
        _snapshot = current;

        PublishStatus(s => s with
        {
            IsScanning = false,
            LastScanUtc = DateTime.UtcNow,
            LastScanSeconds = (DateTime.UtcNow - started).TotalSeconds,
            TrackedApps = current.Count,
            Notice = baseline ? "Baseline taken — changes are reported from now on" : s.Notice
        });
    }
}
