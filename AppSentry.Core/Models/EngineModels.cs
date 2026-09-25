namespace AppSentry.Models;

/// <summary>Engine settings persisted in the database (not per-user UI preferences).</summary>
public sealed record EngineSettings
{
    /// <summary>Safety-net full scan interval. 0 = off (real-time triggers still run).</summary>
    public int ScanIntervalMinutes { get; init; } = 5;

    /// <summary>React to registry/event log/folder changes within seconds instead of waiting for the interval.</summary>
    public bool RealtimeEnabled { get; init; } = true;
}

/// <summary>Snapshot of engine state for status bars and the service pipe.</summary>
public sealed record EngineStatus
{
    public string Mode { get; init; } = "Local";            // "Local" or "Service"
    public bool IsScanning { get; init; }
    public DateTime? LastScanUtc { get; init; }
    public double? LastScanSeconds { get; init; }
    public int TrackedApps { get; init; }
    public string? LastError { get; init; }
    public string? Notice { get; init; }                    // e.g. "Baseline taken" or "Recovered corrupt database"

    /// <summary>Per-source health from the last scan: "ok", "partial: ...", "failed: ...".</summary>
    public Dictionary<string, string> Sources { get; init; } = [];
}
