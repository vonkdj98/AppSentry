using System.Runtime.CompilerServices;
using AppSentry.Core.Backend;
using AppSentry.Core.Engine;
using AppSentry.Core.Sources;
using AppSentry.Models;
using AppSentry.Services;
using AppSentry.ViewModels;

namespace AppSentry.UiTests;

internal static class TestSetup
{
    /// <summary>Tests must never write the real %APPDATA%\AppSentry\ui.json.</summary>
    [ModuleInitializer]
    internal static void KeepSettingsInMemory() => UiSettingsStore.InMemoryOnly = true;
}

/// <summary>An in-memory backend whose tasks complete synchronously, so view models can be driven on one thread.</summary>
internal sealed class FakeBackend : IMonitorBackend
{
    public List<ChangeEvent> Events { get; } = [];
    public List<InstalledApp> Inventory { get; } = [];
    public PersistenceInventory Persistence { get; set; } = new();
    public List<ExclusionEntry> Exclusions { get; set; } = [];
    public EngineSettings Settings { get; set; } = new();
    public int PageRequests { get; private set; }
    public int FullEventRequests { get; private set; }

    public string Mode => "Local";
    public bool CanModify { get; set; } = true;
    public EngineStatus Status { get; set; } = new() { LastScanUtc = DateTime.UtcNow, TrackedApps = 10 };

    public event EventHandler<IReadOnlyList<ChangeEvent>>? EventsDetected;
    public event EventHandler<EngineStatus>? StatusChanged;

    public Task StartAsync() => Task.CompletedTask;

    public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query)
    {
        PageRequests++;
        var ordered = Events.OrderByDescending(e => e.Id).Where(e => query.BeforeId is not { } b || e.Id < b).ToList();
        var page = ordered.Take(query.Limit).Select(e => query.Slim ? MonitorEngine.Slim(e) : e).ToList();
        return Task.FromResult(new HistoryPage { Events = page, HasMore = ordered.Count > page.Count, TotalCount = Events.Count });
    }

    public Task<ChangeEvent?> GetEventAsync(long id)
    {
        FullEventRequests++;
        return Task.FromResult(Events.FirstOrDefault(e => e.Id == id));
    }

    public Task<List<InstalledApp>> GetInventoryAsync() => Task.FromResult(Inventory.ToList());
    public Task<PersistenceInventory> GetPersistenceAsync() => Task.FromResult(Persistence);
    public Task RequestScanAsync() => Task.CompletedTask;
    public Task ClearHistoryAsync() { Events.Clear(); return Task.CompletedTask; }
    public Task<List<ExclusionEntry>> GetExclusionsAsync() => Task.FromResult(Exclusions.ToList());
    public Task SaveExclusionsAsync(List<ExclusionEntry> entries) { Exclusions = entries.ToList(); return Task.CompletedTask; }
    public Task<EngineSettings> GetSettingsAsync() => Task.FromResult(Settings);
    public Task SaveSettingsAsync(EngineSettings settings) { Settings = settings; return Task.CompletedTask; }

    public IReadOnlyList<string> Editions => [];

    public Task<System.Text.Json.JsonElement?> CallEditionAsync(string op, object? data = null) =>
        throw new InvalidOperationException($"No edition handles '{op}' here.");
    public void Dispose() { }

    public void RaiseEvents(IReadOnlyList<ChangeEvent> events) => EventsDetected?.Invoke(this, events);
    public void RaiseStatus(EngineStatus status) => StatusChanged?.Invoke(this, status);

    // ── Builders ──────────────────────────────────────────────────────────────

    private long _nextId = 1;

    public ChangeEvent Add(ChangeType type, string name, TimeSpan ago, DetectionSource source = DetectionSource.Registry,
        string publisher = "Contoso Ltd.", string changedBy = "", Func<ChangeEvent, ChangeEvent>? tweak = null)
    {
        var at = DateTime.UtcNow - ago;
        var app = new InstalledApp
        {
            KeyPath = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name.Replace(" ", "")}",
            Scope = Scopes.Machine64,
            Name = name,
            Version = "2.0",
            Publisher = publisher,
            InstalledFor = "All users",
            RawValues = new() { ["DisplayName"] = name, ["DisplayVersion"] = "2.0", ["Publisher"] = publisher }
        };
        var ev = new ChangeEvent
        {
            Id = _nextId++,
            App = app,
            ChangeType = type,
            DetectedAt = at,
            OccurredAt = at,
            Source = source,
            ChangedBy = changedBy
        };
        if (type == ChangeType.Updated)
        {
            ev = ev with
            {
                PreviousVersion = "1.0",
                PreviousApp = app with { Version = "1.0", RawValues = new() { ["DisplayName"] = name, ["DisplayVersion"] = "1.0", ["Publisher"] = publisher } }
            };
        }
        if (tweak != null) ev = tweak(ev);
        Events.Add(ev);
        return ev;
    }

    /// <summary>A service whose binary moved: a "needs a look" warning.</summary>
    public ChangeEvent AddServiceBinaryChange(string name, TimeSpan ago) => Add(ChangeType.Modified, $"[Windows Service] {name}", ago, DetectionSource.Service,
        tweak: e => e with
        {
            App = e.App with { KeyPath = $@"SERVICE\{name}", Scope = "SERVICES", RawValues = new() { ["ServiceName"] = name, ["ImagePath"] = @"C:\Users\Public\evil.exe", ["ObjectName"] = "LocalSystem", ["Start"] = "Automatic" } },
            PreviousApp = e.App with { KeyPath = $@"SERVICE\{name}", Scope = "SERVICES", RawValues = new() { ["ServiceName"] = name, ["ImagePath"] = @"C:\Program Files\Contoso\agent.exe", ["ObjectName"] = "LocalSystem", ["Start"] = "Automatic" } },
            Details = "ImagePath changed"
        });

    public static ShellViewModel Shell(FakeBackend backend, UiSettings? settings = null)
    {
        var shell = new ShellViewModel(backend, settings ?? new UiSettings());
        shell.InitializeAsync().GetAwaiter().GetResult();
        return shell;
    }
}
