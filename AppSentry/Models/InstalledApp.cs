namespace AppSentry.Models;

/// <summary>
/// Represents a single installed application read from the Windows Registry.
/// </summary>
public record InstalledApp(
    string KeyPath,         // Full registry key path (used as stable ID)
    string Name,            // DisplayName
    string Version,         // DisplayVersion
    string Publisher,       // Publisher
    string InstallDate,     // InstallDate (YYYYMMDD string from registry)
    string InstallLocation, // InstallLocation
    string InstalledBy,     // User who installed it (from HKCU hive or Event Log)
    string InstallSource,   // Source folder the installer ran from (MSI InstallSource)
    string InstallType      // Installer technology: MSI, InnoSetup, NSIS, Store, Unknown
);
