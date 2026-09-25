using AppSentry.Core.Backend;
using AppSentry.Core.Engine;
using AppSentry.Core.Sources;
using AppSentry.Models;

namespace AppSentry.Demo;

/// <summary>
/// Sample data for design review (--demo) and screenshots: this machine's real inventory,
/// services and tasks (read-only), plus a set of made-up changes that exercise every change
/// type and every "needs a look" rule. Nothing is monitored and nothing is written.
/// </summary>
public sealed class DemoBackend : IMonitorBackend
{
    private readonly List<InstalledApp> _inventory;
    private readonly PersistenceInventory _persistence;
    private readonly List<ChangeEvent> _events;
    private List<ExclusionEntry> _exclusions =
    [
        new("Winget Source", true, false),
        new("[Windows Service] PDQInventory-Scanner*", true, false)
    ];
    private EngineSettings _settings = new() { ScanIntervalMinutes = 30, RealtimeEnabled = true };
    private EngineStatus _status;

    /// <param name="synthetic">true: entirely made-up apps, services and tasks (safe for public screenshots).</param>
    public DemoBackend(bool synthetic = false)
    {
        if (synthetic)
        {
            _inventory = SyntheticData.Inventory();
            _persistence = SyntheticData.Persistence();
            _exclusions = [new("Winget Source", true, false), new("Microsoft Edge Update*", true, true)];
        }
        else
        {
            var context = EngineContext.Capture(EngineMode.User);
            var inventory = new InventoryResult();
            try { RegistrySource.Scan(context, inventory); } catch { }
            try { StoreSource.Scan(context, inventory); } catch { }
            _inventory = inventory.Apps.Values.ToList();

            var health = new Dictionary<string, string>();
            var services = ServiceSource.Scan(null, context, DateTime.UtcNow, health).Next;
            var tasks = TaskSource.Scan(null, context, DateTime.UtcNow, health).Next;
            _persistence = new PersistenceInventory
            {
                Services = services.Services.Values.OrderBy(s => s.DisplayName).ToList(),
                Tasks = tasks.Tasks.Values.OrderBy(t => t.Path).ToList()
            };
        }

        _events = BuildEvents();
        _status = new EngineStatus
        {
            Mode = "Service",
            LastScanUtc = DateTime.UtcNow.AddMinutes(-2),
            LastScanSeconds = 0.9,
            TrackedApps = _inventory.Count,
            Sources = new Dictionary<string, string>
            {
                ["Event log"] = "ok", ["Registry"] = "ok", ["Store"] = "ok", ["Scoop"] = "ok",
                ["Folders"] = "ok", ["Services"] = "ok", ["Tasks"] = "ok"
            }
        };
    }

    public string Mode => "Service";
    public bool CanModify => true;
    public EngineStatus Status => _status;

    public event EventHandler<IReadOnlyList<ChangeEvent>>? EventsDetected;
    public event EventHandler<EngineStatus>? StatusChanged;

    public Task StartAsync() => Task.CompletedTask;
    public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query)
    {
        var ordered = _events.OrderByDescending(e => e.Id).Where(e => query.BeforeId is not { } before || e.Id < before).ToList();
        var page = ordered.Take(query.Limit).ToList();
        return Task.FromResult(new HistoryPage { Events = page, HasMore = ordered.Count > page.Count, TotalCount = _events.Count });
    }

    public Task<ChangeEvent?> GetEventAsync(long id) => Task.FromResult(_events.FirstOrDefault(e => e.Id == id));
    public Task<List<InstalledApp>> GetInventoryAsync() => Task.FromResult(_inventory.ToList());
    public Task<PersistenceInventory> GetPersistenceAsync() => Task.FromResult(_persistence);
    public Task<List<ExclusionEntry>> GetExclusionsAsync() => Task.FromResult(_exclusions.ToList());
    public Task SaveExclusionsAsync(List<ExclusionEntry> entries) { _exclusions = entries.ToList(); return Task.CompletedTask; }
    public Task<EngineSettings> GetSettingsAsync() => Task.FromResult(_settings);
    public Task SaveSettingsAsync(EngineSettings settings) { _settings = settings; return Task.CompletedTask; }

    public Task ClearHistoryAsync()
    {
        _events.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Pretends to scan for a second so the status pill can be seen changing.</summary>
    public async Task RequestScanAsync()
    {
        _status = _status with { IsScanning = true };
        StatusChanged?.Invoke(this, _status);
        await Task.Delay(1200);
        _status = _status with { IsScanning = false, LastScanUtc = DateTime.UtcNow };
        StatusChanged?.Invoke(this, _status);
        EventsDetected?.Invoke(this, []);
    }

    public void Dispose() { }

    // ── Sample changes ───────────────────────────────────────────────────────

    private List<ChangeEvent> BuildEvents()
    {
        var now = DateTime.UtcNow;
        var real = _inventory
            .Where(a => a.RawValues?.ContainsKey("DisplayIcon") == true && a.Version.Length > 0 && Scopes.IsRegistry(a.Scope))
            .OrderBy(a => a.Name)
            .ToList();
        var store = _inventory.Where(a => a.PackageFullName.Length > 0).OrderBy(a => a.Name).ToList();
        InstalledApp Pick(int i) => real.Count > 0 ? real[i % real.Count] : new InstalledApp { Name = $"Sample App {i}", Version = "1.0", Scope = Scopes.Machine64 };
        InstalledApp Named(string prefix, int fallback) =>
            real.FirstOrDefault(a => a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? Pick(fallback);

        var events = new List<ChangeEvent>();
        long id = 1000;
        void Add(ChangeEvent ev) => events.Add(ev with { Id = ++id });

        // Today
        var updated = Named("7-Zip", 0);
        Add(new ChangeEvent
        {
            App = updated with { InstalledBy = @"CONTOSO\alex" },
            PreviousApp = updated with { Version = Older(updated.Version), RawValues = WithVersion(updated.RawValues, Older(updated.Version)) },
            PreviousVersion = Older(updated.Version),
            ChangeType = ChangeType.Updated,
            ChangedBy = @"CONTOSO\alex",
            DetectedAt = now.AddMinutes(-18),
            OccurredAt = now.AddMinutes(-19),
            Source = DetectionSource.Registry,
            SizeBytes = updated.EstimatedSizeKb * 1024,
            Details = @"MSI event 1033 by CONTOSO\alex"
        });
        Add(Service("Contoso Remote Agent", "ContosoAgent", ChangeType.Modified, now.AddMinutes(-47),
            before: @"""C:\Program Files\Contoso\Agent\agent.exe"" -service",
            after: @"""C:\Users\Public\Libraries\agent.exe"" -service",
            details: @"ImagePath: ""C:\Program Files\Contoso\Agent\agent.exe"" → ""C:\Users\Public\Libraries\agent.exe"""));
        var installed = Named("Zoom", 3);
        Add(new ChangeEvent
        {
            App = installed with { InstalledBy = @"NT AUTHORITY\SYSTEM" },
            ChangeType = ChangeType.Installed,
            ChangedBy = @"NT AUTHORITY\SYSTEM",
            DetectedAt = now.AddHours(-2),
            OccurredAt = now.AddHours(-2).AddMinutes(-1),
            Source = DetectionSource.Registry,
            SizeBytes = installed.EstimatedSizeKb * 1024,
            Details = "MSI event 1033 by NT AUTHORITY\\SYSTEM"
        });
        Add(TaskEvent(@"\Contoso\UpdaterTaskMachineCore", ChangeType.Installed, now.AddHours(-2).AddMinutes(-2), "SYSTEM",
            @"C:\Program Files (x86)\Contoso\Update\ContosoUpdate.exe /c"));
        Add(new ChangeEvent
        {
            App = new InstalledApp { KeyPath = @"EVENTLOG\MSI\Contoso VPN Client", Name = "Contoso VPN Client", Version = "4.2.1", Publisher = "Contoso Ltd.", InstallType = "MSI" },
            ChangeType = ChangeType.Failed,
            ChangedBy = @"CONTOSO\helpdesk",
            DetectedAt = now.AddHours(-4),
            OccurredAt = now.AddHours(-4),
            Source = DetectionSource.EventLog,
            Details = "Installation failed with status 1603 [MSI event 1033]"
        });

        // Yesterday
        foreach (var (app, hours) in new[] { (Named("Google Chrome", 5), 20), (Named("Mozilla Firefox", 8), 22) })
        {
            Add(new ChangeEvent
            {
                App = app,
                PreviousApp = app with { Version = Older(app.Version), RawValues = WithVersion(app.RawValues, Older(app.Version)) },
                PreviousVersion = Older(app.Version),
                ChangeType = ChangeType.Updated,
                DetectedAt = now.AddHours(-hours),
                OccurredAt = now.AddHours(-hours),
                Source = DetectionSource.Registry
            });
        }
        Add(new ChangeEvent
        {
            App = new InstalledApp { KeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C0A1-DEMO}", Name = "Contoso Endpoint Protection", Version = "12.4.2", Publisher = "Contoso Security", Scope = Scopes.Machine64, InstallType = "MSI" },
            ChangeType = ChangeType.Removed,
            ChangedBy = @"CONTOSO\sam",
            DetectedAt = now.AddHours(-26),
            OccurredAt = now.AddHours(-26),
            Source = DetectionSource.Registry,
            Details = @"MSI event 1034 by CONTOSO\sam"
        });
        Add(Service("Contoso Filter Driver", "cfltr", ChangeType.Installed, now.AddHours(-27), after: @"\SystemRoot\System32\drivers\cfltr.sys", driver: true));
        var removed = Named("VLC", 11);
        Add(new ChangeEvent { App = removed, ChangeType = ChangeType.Removed, DetectedAt = now.AddHours(-30), Source = DetectionSource.Registry });

        // Earlier this week
        if (store.Count > 0)
        {
            var s = store[0];
            Add(new ChangeEvent { App = s, PreviousApp = s, PreviousVersion = Older(s.Version), ChangeType = ChangeType.Updated, DetectedAt = now.AddDays(-3), OccurredAt = now.AddDays(-3), Source = DetectionSource.Store });
        }
        Add(new ChangeEvent
        {
            App = new InstalledApp { KeyPath = @"FILESYSTEM\C:\Program Files\PortableTool", Scope = "FILESYSTEM", Name = "PortableTool", InstallLocation = @"C:\Program Files\PortableTool", InstallType = "Portable/Unknown", InstalledFor = "All users" },
            ChangeType = ChangeType.Installed,
            DetectedAt = now.AddDays(-3).AddHours(-2),
            Source = DetectionSource.FileSystem,
            Details = "New folder with no matching Add/Remove Programs entry"
        });
        var later = Named("Wireshark", 14);
        Add(new ChangeEvent { App = later, ChangeType = ChangeType.Installed, DetectedAt = now.AddDays(-4), OccurredAt = now.AddDays(-4), Source = DetectionSource.Registry, ChangedBy = @"CONTOSO\alex" });

        return events.OrderByDescending(e => e.DetectedAt).ToList();
    }

    private static ChangeEvent Service(string display, string name, ChangeType type, DateTime at, string after, string? before = null, string details = "", bool driver = false)
    {
        InstalledApp App(string imagePath) => new()
        {
            KeyPath = $@"{(driver ? "DRIVER" : "SERVICE")}\{name}",
            Scope = driver ? "DRIVERS" : "SERVICES",
            Name = $"[{(driver ? "Driver" : "Windows Service")}] {display}",
            InstallType = driver ? "Driver" : "Windows Service",
            InstalledFor = "All users",
            InstallSource = $"Service: {name} | Start: Automatic | Account: LocalSystem | Path: {imagePath}",
            RawValues = new Dictionary<string, string> { ["ServiceName"] = name, ["ImagePath"] = imagePath, ["Start"] = "Automatic", ["ObjectName"] = driver ? "" : "LocalSystem" }
        };
        return new ChangeEvent
        {
            App = App(after),
            PreviousApp = before == null ? null : App(before),
            ChangeType = type,
            DetectedAt = at,
            Source = driver ? DetectionSource.Driver : DetectionSource.Service,
            Details = details.Length > 0 ? details : $"Service: {name} | Start: Automatic | Path: {after}"
        };
    }

    private static ChangeEvent TaskEvent(string path, ChangeType type, DateTime at, string runAs, string actions) => new()
    {
        App = new InstalledApp
        {
            KeyPath = $@"SCHEDULEDTASK\{path}",
            Scope = "TASKS",
            Name = $"[Scheduled Task] {path}",
            Publisher = "Contoso Ltd.",
            InstallType = "Scheduled Task",
            InstalledFor = runAs,
            InstallSource = $"Runs: {actions} | As: {runAs}",
            RawValues = new Dictionary<string, string> { ["Path"] = path, ["RunAs"] = runAs, ["Actions"] = actions, ["Author"] = "Contoso Ltd." }
        },
        ChangeType = type,
        DetectedAt = at,
        Source = DetectionSource.ScheduledTask,
        Details = "New scheduled task"
    };

    private static string Older(string version)
    {
        var parts = version.Split('.');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(parts[i], out var n) && n > 0)
            {
                parts[i] = (n - 1).ToString().PadLeft(parts[i].Length, '0');
                return string.Join('.', parts);
            }
        }
        return version + "-beta";
    }

    private static Dictionary<string, string>? WithVersion(Dictionary<string, string>? values, string version)
    {
        if (values == null) return null;
        var copy = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase) { ["DisplayVersion"] = version };
        return copy;
    }
}
