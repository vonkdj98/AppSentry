using AppSentry.Models;

namespace AppSentry.Core.Backend;

/// <summary>
/// What the UI talks to. <see cref="LocalBackend"/> runs the engine in-process;
/// PipeBackend talks to the AppSentry Windows service. The UI doesn't know which it has.
/// Events are raised on a background thread — marshal to the UI thread before touching controls.
/// </summary>
public interface IMonitorBackend : IDisposable
{
    /// <summary>"Local" or "Service".</summary>
    string Mode { get; }

    /// <summary>False when connected to the service as a non-admin account (read-only).</summary>
    bool CanModify { get; }

    EngineStatus Status { get; }

    event EventHandler<IReadOnlyList<ChangeEvent>>? EventsDetected;

    event EventHandler<EngineStatus>? StatusChanged;

    Task StartAsync();

    Task<List<ChangeEvent>> GetHistoryAsync();

    Task<List<InstalledApp>> GetInventoryAsync();

    /// <summary>Services, drivers and scheduled tasks the engine currently tracks.</summary>
    Task<Sources.PersistenceInventory> GetPersistenceAsync();

    /// <summary>Queues a scan; completion is reported through <see cref="StatusChanged"/>.</summary>
    Task RequestScanAsync();

    Task ClearHistoryAsync();

    Task<List<ExclusionEntry>> GetExclusionsAsync();

    Task SaveExclusionsAsync(List<ExclusionEntry> entries);

    Task<EngineSettings> GetSettingsAsync();

    Task SaveSettingsAsync(EngineSettings settings);
}
