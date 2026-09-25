namespace AppSentry.Models;

/// <summary>Engine settings persisted in the database (not per-user UI preferences).</summary>
public sealed record EngineSettings
{
    /// <summary>Safety-net full scan interval. 0 = off (real-time triggers still run).</summary>
    public int ScanIntervalMinutes { get; init; } = 5;

    /// <summary>React to registry/event log/folder changes within seconds instead of waiting for the interval.</summary>
    public bool RealtimeEnabled { get; init; } = true;

    /// <summary>Delete changes older than this many days. 0 = keep everything (the default: nothing is deleted unless you choose to).</summary>
    public int RetentionDays { get; init; }
}

/// <summary>A request for one page of history, newest first.</summary>
public sealed record HistoryQuery
{
    /// <summary>Return events with an id lower than this (the last id of the previous page). Null = start from the newest.</summary>
    public long? BeforeId { get; init; }

    public int Limit { get; init; } = 1000;

    /// <summary>
    /// Leave out the full before/after registry values of app events (the bulk of each event).
    /// The details pane asks for the full event by id when it's opened.
    /// </summary>
    public bool Slim { get; init; } = true;
}

public sealed record HistoryPage
{
    public List<ChangeEvent> Events { get; init; } = [];
    public bool HasMore { get; init; }
    public int TotalCount { get; init; }
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
