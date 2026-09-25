using AppSentry.Core.Engine;
using AppSentry.Models;

namespace AppSentry.Core.Backend;

/// <summary>Runs the engine inside the tray app, as the signed-in user. Data lives in %APPDATA%\AppSentry.</summary>
public sealed class LocalBackend : IMonitorBackend
{
    private readonly MonitorEngine _engine;

    public LocalBackend(string dataDir)
    {
        _engine = new MonitorEngine(new EngineOptions
        {
            DataDir = dataDir,
            Mode = EngineMode.User,
            LegacyDir = dataDir // v1 kept its JSON files in the same folder
        });
        _engine.EventsDetected += (s, e) => EventsDetected?.Invoke(this, e);
        _engine.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
    }

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppSentry");

    public string Mode => "Local";

    public bool CanModify => true;

    public EngineStatus Status => _engine.Status;

    public event EventHandler<IReadOnlyList<ChangeEvent>>? EventsDetected;

    public event EventHandler<EngineStatus>? StatusChanged;

    public Task StartAsync() => Task.Run(_engine.Start);

    public Task<List<ChangeEvent>> GetHistoryAsync() => Task.Run(_engine.GetHistory);

    public Task<List<InstalledApp>> GetInventoryAsync() => Task.Run(_engine.GetInventory);

    public Task<Sources.PersistenceInventory> GetPersistenceAsync() => Task.Run(_engine.GetPersistence);

    public Task RequestScanAsync()
    {
        _engine.RequestScan("manual", TimeSpan.Zero);
        return Task.CompletedTask;
    }

    public Task ClearHistoryAsync() => Task.Run(_engine.ClearHistory);

    public Task<List<ExclusionEntry>> GetExclusionsAsync() => Task.FromResult(_engine.GetExclusions().ToList());

    public Task SaveExclusionsAsync(List<ExclusionEntry> entries) => Task.Run(() => _engine.SetExclusions(entries));

    public Task<EngineSettings> GetSettingsAsync() => Task.FromResult(_engine.Settings);

    public Task SaveSettingsAsync(EngineSettings settings) => Task.Run(() => _engine.UpdateSettings(settings));

    public void Dispose() => _engine.Dispose();
}
