namespace AppSentry.Models;

/// <summary>
/// One installed application as seen by an inventory source (registry, Store, Scoop).
/// The first nine properties match the original v1 model so old history.json files
/// deserialize unchanged; everything after them was added by the v2 engine.
/// </summary>
public sealed record InstalledApp
{
    public string KeyPath { get; init; } = "";         // Stable ID, e.g. HKLM\SOFTWARE\...\Uninstall\{GUID}
    public string Name { get; init; } = "";            // DisplayName
    public string Version { get; init; } = "";         // DisplayVersion
    public string Publisher { get; init; } = "";
    public string InstallDate { get; init; } = "";     // YYYYMMDD as the registry stores it
    public string InstallLocation { get; init; } = "";
    public string InstalledBy { get; init; } = "";     // Who ran the install, when known (MSI event log)
    public string InstallSource { get; init; } = "";   // Folder the installer ran from, or a source label
    public string InstallType { get; init; } = "";     // Installer technology: MSI, InnoSetup, NSIS, Store, ...

    /// <summary>Which inventory scope produced this entry: HKLM64, HKLM32, HKU\{sid}, STORE\{sid}, SCOOP\{id}.</summary>
    public string Scope { get; init; } = "";

    /// <summary>"All users" for machine-wide installs, otherwise the account the per-user install belongs to.</summary>
    public string InstalledFor { get; init; } = "";

    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";

    /// <summary>MSI ProductCode ({GUID}) when the entry is a Windows Installer product.</summary>
    public string ProductCode { get; init; } = "";

    public string PackageFullName { get; init; } = "";
    public string PackageFamilyName { get; init; } = "";

    /// <summary>Package manager that owns this app, e.g. "Chocolatey (git)" or "Winget (Git.Git)".</summary>
    public string PackageManager { get; init; } = "";

    /// <summary>EstimatedSize from the uninstall key, in KB.</summary>
    public long? EstimatedSizeKb { get; init; }

    /// <summary>Last write time of the uninstall key — a good proxy for when the install/update happened.</summary>
    public DateTime? KeyLastWriteUtc { get; init; }

    /// <summary>Every value under the uninstall key, stringified. Used by Diff View and Modified detection.</summary>
    public Dictionary<string, string>? RawValues { get; init; }
}
