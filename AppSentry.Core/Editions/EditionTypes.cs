using System.Text.Json;
using AppSentry.Models;

namespace AppSentry.Core.Editions;

// Extension points for editions built on top of AppSentry.
//
// An edition is extra source compiled into these same assemblies (see AppSentry.Edition.props in
// Directory.Build.props). It implements the empty partial methods declared on MonitorEngine,
// AttentionClassifier and the UI's ShellViewModel. This open-source build implements none of
// them, so they compile away and behave as if they weren't there.

/// <summary>A call from the UI to an edition, e.g. op "x.status". Routed over the pipe in service mode.</summary>
public sealed record EditionRequest(string Op, JsonElement? Data, bool CallerIsAdmin, string CallerName);

public sealed record EditionResponse(bool Ok, JsonElement? Data = null, string? Error = null)
{
    public static EditionResponse Success<T>(T data) =>
        new(true, JsonSerializer.SerializeToElement(data, Util.AppJson.Options));

    public static EditionResponse Success() => new(true);

    public static EditionResponse Failure(string error) => new(false, null, error);
}

/// <summary>
/// What an edition contributes to one scan. Its events and state are committed in the same
/// transaction as the built-in sources; <see cref="OnCommitted"/> runs only after that succeeds,
/// so an edition can advance its in-memory baselines exactly when the core does.
/// </summary>
public sealed class EditionScan(DateTime startedUtc, Dictionary<string, string> health)
{
    public DateTime StartedUtc { get; } = startedUtc;

    /// <summary>Source health shown on the status panel ("ok", "failed: …").</summary>
    public Dictionary<string, string> Health { get; } = health;

    public List<ChangeEvent> Events { get; } = [];

    /// <summary>State key → value to persist (serialized as JSON, written only when changed).</summary>
    public Dictionary<string, object> State { get; } = [];

    public List<Action> OnCommitted { get; } = [];
}
