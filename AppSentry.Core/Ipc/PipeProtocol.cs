using System.Text.Json;

namespace AppSentry.Core.Ipc;

/// <summary>
/// Tray app ↔ service protocol over \\.\pipe\AppSentry: one JSON object per line.
///   request   { "Id": 7, "Op": "history" }                 client → service
///   response  { "Id": 7, "Ok": true, "Data": [...] }        service → client
///   push      { "Push": "events", "Data": [...] }           service → client, unsolicited
/// </summary>
public static class PipeProtocol
{
    public const string DefaultPipeName = "AppSentry";
    public const int Version = 1;

    public static class Ops
    {
        public const string Hello = "hello";
        public const string History = "history";
        public const string Inventory = "inventory";
        public const string Persistence = "persistence";
        public const string Scan = "scan";
        public const string ClearHistory = "clearHistory";
        public const string GetExclusions = "getExclusions";
        public const string SetExclusions = "setExclusions";
        public const string GetSettings = "getSettings";
        public const string SetSettings = "setSettings";

        /// <summary>Operations that change state; only admin accounts may call them. (Asking for a scan is harmless.)</summary>
        public static readonly HashSet<string> Mutating = [ClearHistory, SetExclusions, SetSettings];
    }

    public static class Pushes
    {
        public const string Events = "events";
        public const string Status = "status";
    }
}

public sealed record PipeMessage
{
    public int? Id { get; init; }
    public string? Op { get; init; }
    public string? Push { get; init; }
    public bool? Ok { get; init; }
    public string? Error { get; init; }
    public JsonElement? Data { get; init; }
}

public sealed record HelloResponse
{
    public int ProtocolVersion { get; init; }
    public string ServiceVersion { get; init; } = "";
    public bool CanModify { get; init; }
    public string ClientName { get; init; } = "";
    public Models.EngineStatus Status { get; init; } = new();
}
