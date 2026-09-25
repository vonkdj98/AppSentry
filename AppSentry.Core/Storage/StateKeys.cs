namespace AppSentry.Core.Storage;

/// <summary>Keys in the <c>state</c> table.</summary>
public static class StateKeys
{
    public const string Settings = "settings";
    public const string Exclusions = "exclusions";
    public const string Snapshot = "inventory.snapshot";
    public const string KnownScopes = "inventory.scopes";
    public const string MsiBookmark = "eventlog.msi";
    public const string FileSystem = "fs.baseline";
    public const string Services = "services.baseline";
    public const string Tasks = "tasks.baseline";
}
