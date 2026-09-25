using AppSentry.Models;

namespace AppSentry.Core.Sources;

/// <summary>
/// What the inventory sources (registry, Store, Scoop) saw in one scan.
///
/// <see cref="CompletedScopes"/> is the key to avoiding false removals: a scope is only listed
/// when it was enumerated in full. A scope that failed (access denied, hive not loaded, Store
/// API error) is simply absent, and the change detector carries its previous entries forward
/// instead of reporting every app in it as removed.
/// </summary>
public sealed class InventoryResult
{
    public Dictionary<string, InstalledApp> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> CompletedScopes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-source health for the status bar: "ok", "partial: …", "failed: …".</summary>
    public Dictionary<string, string> Health { get; } = [];

    public void Add(InstalledApp app) => Apps[app.KeyPath] = app;
}

/// <summary>Scope naming and grouping shared by sources and the detector.</summary>
public static class Scopes
{
    public const string Machine64 = "HKLM64";
    public const string Machine32 = "HKLM32";

    public static string User(string sid) => $@"HKU\{sid}";

    public static string Store(string sid) => $@"STORE\{sid}";

    public static string Scoop(string id) => $@"SCOOP\{id}";

    /// <summary>
    /// Scopes an app can legitimately move between during an upgrade. 64↔32-bit machine
    /// installs count as the same place; different users or sources never do.
    /// </summary>
    public static string Group(string scope) =>
        scope is Machine64 or Machine32 ? "machine" : scope.ToUpperInvariant();

    public static DetectionSource SourceOf(string scope) =>
        scope.StartsWith(@"STORE\", StringComparison.OrdinalIgnoreCase) ? DetectionSource.Store :
        scope.StartsWith(@"SCOOP\", StringComparison.OrdinalIgnoreCase) ? DetectionSource.PackageManager :
        DetectionSource.Registry;

    public static bool IsRegistry(string scope) => SourceOf(scope) == DetectionSource.Registry;
}
