namespace AppSentry.Core.Engine;

public enum EngineMode
{
    /// <summary>In-process engine running as the signed-in user (tray app without the service).</summary>
    User,

    /// <summary>Hosted by the AppSentry Windows service as LocalSystem.</summary>
    Service
}

public sealed record EngineOptions
{
    /// <summary>Where appsentry.db and engine.log live.</summary>
    public required string DataDir { get; init; }

    public EngineMode Mode { get; init; } = EngineMode.User;

    /// <summary>Folder holding v1 JSON files to import once (null = none).</summary>
    public string? LegacyDir { get; init; }
}
