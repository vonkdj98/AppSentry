using System.Diagnostics;
using System.Security.Principal;
using AppTracker.Models;
using Microsoft.Win32;

namespace AppTracker;

/// <summary>
/// Reads all installed applications from:
///   1. HKLM Uninstall keys (64-bit + WOW6432)
///   2. HKCU Uninstall keys (current user)
///   3. HKU\{SID} Uninstall keys (other user accounts — requires admin)
///   4. Microsoft Store / AppX packages (via PowerShell Get-AppxPackage)
/// Returns a dictionary keyed by a stable identity string.
/// </summary>
public static class RegistryScanner
{
    private static readonly string[] UninstallPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    /// <summary>
    /// Scans all installed apps. Returns a snapshot dictionary keyed by key path.
    /// </summary>
    public static Dictionary<string, InstalledApp> Scan()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        // 1. HKLM (64-bit and 32-bit WOW6432 node)
        foreach (var path in UninstallPaths)
        {
            ReadHive(Registry.LocalMachine, path, "HKLM", null, apps);
        }

        // 2. HKCU (current user per-user installs)
        ReadHive(Registry.CurrentUser, UninstallPaths[0], "HKCU", Environment.UserName, apps);

        // 3. Other users' HKCU via HKU\{SID} (requires admin elevation)
        ScanOtherUsers(apps);

        // 4. Microsoft Store / AppX packages
        ScanStoreApps(apps);

        return apps;
    }

    // ── Registry hive reader ─────────────────────────────────────────────────

    private static void ReadHive(
        RegistryKey hive,
        string subKeyPath,
        string hivePrefix,
        string? userName,
        Dictionary<string, InstalledApp> apps)
    {
        try
        {
            using var root = hive.OpenSubKey(subKeyPath, writable: false);
            if (root == null) return;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(subKeyName, writable: false);
                    if (key == null) continue;

                    var name = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Skip system components and patches
                    var systemComponent = key.GetValue("SystemComponent");
                    if (systemComponent is int sc && sc == 1) continue;

                    var version = key.GetValue("DisplayVersion") as string ?? "";
                    var publisher = key.GetValue("Publisher") as string ?? "";
                    var installDate = key.GetValue("InstallDate") as string ?? "";
                    var installLocation = key.GetValue("InstallLocation") as string ?? "";
                    var installSource = key.GetValue("InstallSource") as string ?? "";
                    var uninstallString = key.GetValue("UninstallString") as string ?? "";
                    var quietUninstall = key.GetValue("QuietUninstallString") as string ?? "";
                    var windowsInstaller = key.GetValue("WindowsInstaller");

                    // Determine who installed it
                    var installedBy = userName ?? "All Users (Admin)";

                    // Determine installer technology
                    var installType = DetectInstallType(
                        uninstallString, quietUninstall, windowsInstaller, subKeyName, installSource);

                    var keyPath = $"{hivePrefix}\\{subKeyPath}\\{subKeyName}";

                    var app = new InstalledApp(
                        KeyPath: keyPath,
                        Name: name.Trim(),
                        Version: version.Trim(),
                        Publisher: publisher.Trim(),
                        InstallDate: installDate.Trim(),
                        InstallLocation: installLocation.Trim(),
                        InstalledBy: installedBy,
                        InstallSource: installSource.Trim(),
                        InstallType: installType
                    );

                    apps[keyPath] = app;
                }
                catch (Exception)
                {
                    // Skip inaccessible keys silently
                }
            }
        }
        catch (Exception)
        {
            // Skip inaccessible hives silently
        }
    }

    // ── Other users' registries (HKU\{SID}) ─────────────────────────────────

    /// <summary>
    /// Enumerates all user SIDs under HKU and reads their Uninstall keys.
    /// This catches per-user installs done by other accounts on the machine.
    /// Requires admin elevation to access other users' hives.
    /// </summary>
    private static void ScanOtherUsers(Dictionary<string, InstalledApp> apps)
    {
        try
        {
            using var hku = Registry.Users;
            var currentSid = WindowsIdentity.GetCurrent().User?.Value ?? "";

            foreach (var sidName in hku.GetSubKeyNames())
            {
                // Skip non-user SIDs (like .DEFAULT, S-1-5-18, _Classes)
                if (!sidName.StartsWith("S-1-5-21-")) continue;
                if (sidName.EndsWith("_Classes")) continue;

                // Skip current user — already scanned via HKCU
                if (sidName.Equals(currentSid, StringComparison.OrdinalIgnoreCase)) continue;

                var userName = ResolveUserName(sidName);
                var hivePrefix = $"HKU\\{sidName}";

                using var userKey = hku.OpenSubKey(sidName, writable: false);
                if (userKey == null) continue;

                var uninstallPath = UninstallPaths[0]; // Only the main path (no WOW6432 under HKU typically)
                var fullPath = $"{sidName}\\{uninstallPath}";

                try
                {
                    using var root = hku.OpenSubKey(fullPath, writable: false);
                    if (root == null) continue;

                    foreach (var subKeyName in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = root.OpenSubKey(subKeyName, writable: false);
                            if (key == null) continue;

                            var name = key.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            var systemComponent = key.GetValue("SystemComponent");
                            if (systemComponent is int sc && sc == 1) continue;

                            var version = key.GetValue("DisplayVersion") as string ?? "";
                            var publisher = key.GetValue("Publisher") as string ?? "";
                            var installDate = key.GetValue("InstallDate") as string ?? "";
                            var installLocation = key.GetValue("InstallLocation") as string ?? "";
                            var installSource = key.GetValue("InstallSource") as string ?? "";
                            var uninstallString = key.GetValue("UninstallString") as string ?? "";
                            var quietUninstall = key.GetValue("QuietUninstallString") as string ?? "";
                            var windowsInstaller = key.GetValue("WindowsInstaller");

                            var installType = DetectInstallType(
                                uninstallString, quietUninstall, windowsInstaller, subKeyName, installSource);

                            var keyPath = $"{hivePrefix}\\{uninstallPath}\\{subKeyName}";

                            var app = new InstalledApp(
                                KeyPath: keyPath,
                                Name: name.Trim(),
                                Version: version.Trim(),
                                Publisher: publisher.Trim(),
                                InstallDate: installDate.Trim(),
                                InstallLocation: installLocation.Trim(),
                                InstalledBy: userName,
                                InstallSource: installSource.Trim(),
                                InstallType: installType
                            );

                            apps[keyPath] = app;
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // Not running as admin — can't access HKU. Silently skip.
        }
    }

    /// <summary>
    /// Resolves a SID string to a human-readable username (DOMAIN\User).
    /// Falls back to the raw SID if lookup fails.
    /// </summary>
    private static string ResolveUserName(string sid)
    {
        try
        {
            var secId = new SecurityIdentifier(sid);
            var account = secId.Translate(typeof(NTAccount)) as NTAccount;
            if (account != null)
            {
                // Return just the username part (strip DOMAIN\)
                var fullName = account.Value;
                var slashIdx = fullName.IndexOf('\\');
                return slashIdx >= 0 ? fullName[(slashIdx + 1)..] : fullName;
            }
        }
        catch (Exception) { }
        return sid; // fallback to raw SID
    }

    // ── Microsoft Store / AppX packages ──────────────────────────────────────

    /// <summary>
    /// Scans Microsoft Store apps using PowerShell Get-AppxPackage.
    /// Returns UWP/MSIX apps that don't appear in the traditional registry.
    /// </summary>
    private static void ScanStoreApps(Dictionary<string, InstalledApp> apps)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Select-Object Name, PackageFullName, Version, Publisher, InstallLocation, SignatureKind, IsFramework | ConvertTo-Csv -NoTypeInformation\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000); // 15 second timeout

            if (string.IsNullOrWhiteSpace(output)) return;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return; // header + at least 1 row

            // Parse CSV: "Name","PackageFullName","Version","Publisher","InstallLocation","SignatureKind","IsFramework"
            for (int i = 1; i < lines.Length; i++)
            {
                try
                {
                    var fields = ParseCsvLine(lines[i]);
                    if (fields.Length < 7) continue;

                    var packageName = fields[0];
                    var fullName = fields[1];
                    var version = fields[2];
                    var publisher = fields[3];
                    var installLocation = fields[4];
                    var signatureKind = fields[5];
                    var isFramework = fields[6];

                    // Skip framework packages (runtime dependencies, not user-facing apps)
                    if (isFramework.Equals("True", StringComparison.OrdinalIgnoreCase)) continue;

                    // Skip known non-app packages (system components)
                    if (IsSystemStorePackage(packageName)) continue;

                    var keyPath = $"STORE\\{fullName}";

                    // Skip if we already have this from the registry
                    if (apps.ContainsKey(keyPath)) continue;

                    // Make the publisher name readable (strip CN= prefix and hash suffix)
                    var cleanPublisher = CleanStorePublisher(publisher);

                    // Make the name more readable
                    var displayName = CleanStoreAppName(packageName);

                    var installSource = signatureKind switch
                    {
                        "Store" => "Microsoft Store",
                        "System" => "Windows (Built-in)",
                        "Developer" => "Sideloaded",
                        _ => "Store/Sideloaded"
                    };

                    var app = new InstalledApp(
                        KeyPath: keyPath,
                        Name: displayName,
                        Version: version.Trim(),
                        Publisher: cleanPublisher,
                        InstallDate: "",
                        InstallLocation: installLocation.Trim(),
                        InstalledBy: Environment.UserName,
                        InstallSource: installSource,
                        InstallType: "Store"
                    );

                    apps[keyPath] = app;
                }
                catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // PowerShell not available or failed — silently skip
        }
    }

    /// <summary>
    /// Parses a CSV line handling quoted fields that may contain commas.
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = "";
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.Trim());
                current = "";
            }
            else
            {
                current += c;
            }
        }
        fields.Add(current.Trim());
        return fields.ToArray();
    }

    /// <summary>
    /// Converts Store package names like "Microsoft.WindowsCalculator" to "Windows Calculator".
    /// </summary>
    private static string CleanStoreAppName(string packageName)
    {
        // Remove common prefixes
        var name = packageName;
        foreach (var prefix in new[] { "Microsoft.", "Windows.", "MicrosoftWindows.", "Microsoft.Windows." })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        // Insert spaces before capital letters (PascalCase → readable)
        var readable = System.Text.RegularExpressions.Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");

        // Clean up underscores and dots
        readable = readable.Replace("_", " ").Replace(".", " ").Trim();

        return string.IsNullOrWhiteSpace(readable) ? packageName : readable;
    }

    /// <summary>
    /// Cleans publisher strings like "CN=Microsoft Corporation, O=..." to just "Microsoft Corporation".
    /// </summary>
    private static string CleanStorePublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "";

        // Handle "CN=Publisher Name, O=..." format
        if (publisher.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            var rest = publisher[3..];
            var commaIdx = rest.IndexOf(',');
            return commaIdx >= 0 ? rest[..commaIdx].Trim() : rest.Trim();
        }

        return publisher.Trim();
    }

    /// <summary>
    /// Filters out known system/framework Store packages that aren't user-facing apps.
    /// </summary>
    private static bool IsSystemStorePackage(string packageName)
    {
        var lower = packageName.ToLowerInvariant();
        var systemPrefixes = new[]
        {
            "microsoft.net.",
            "microsoft.vclibs",
            "microsoft.ui.xaml",
            "microsoft.directx",
            "microsoft.services.",
            "microsoft.advertising",
            "microsoft.windows.cloudexperiencehost",
            "microsoft.windows.contentdeliverymanager",
            "microsoft.windows.oobenetworkconnectionflow",
            "microsoft.windows.parentalcontrols",
            "microsoft.windows.capturepicker",
            "microsoft.windows.pinningconfirmationdialog",
            "microsoft.windows.secureassessmentbrowser",
            "microsoft.windows.search",
            "microsoft.windows.appresolverux",
            "microsoft.windows.assignedaccesslockapp",
            "microsoft.aad.brokerplugin",
            "microsoft.accountscontrol",
            "microsoft.asynctextservice",
            "microsoft.bioentrollment",
            "microsoft.creddialohost",
            "microsoft.ecapp",
            "microsoft.lockapp",
            "microsoft.mpi.",
            "windows.cbspreview",
            "windows.immersivecontrolpanel",
            "windows.printdialog",
            "inputapp",
            "narratorquickstart",
            "microsoft.windows.startmenuexperiencehost",
            "microsoft.windows.shellexperiencehost",
            "microsoft.windowscommunicationsapps", // built-in mail/calendar (keep? borderline)
        };

        foreach (var prefix in systemPrefixes)
        {
            if (lower.StartsWith(prefix)) return true;
        }

        return false;
    }

    // ── Install type detection ───────────────────────────────────────────────

    /// <summary>
    /// Detects the installer technology from registry clues.
    /// </summary>
    private static string DetectInstallType(
        string uninstallString,
        string quietUninstallString,
        object? windowsInstaller,
        string subKeyName,
        string installSource)
    {
        var uninstallLower = uninstallString.ToLowerInvariant();
        var quietLower = quietUninstallString.ToLowerInvariant();

        // Windows Installer (MSI)
        if (windowsInstaller is int wi && wi == 1)
            return "MSI";
        if (uninstallLower.Contains("msiexec"))
            return "MSI";

        // Microsoft Store / MSIX / AppX
        if (subKeyName.Contains("_") && subKeyName.Contains("!"))
            return "Store";
        if (uninstallLower.Contains("windowsapps"))
            return "Store";

        // InnoSetup — uses unins000.exe pattern
        if (uninstallLower.Contains("unins00"))
            return "InnoSetup";

        // NSIS — common uninstall.exe or uninst.exe pattern
        if (uninstallLower.Contains("uninstall.exe") || uninstallLower.Contains("uninst.exe"))
            return "NSIS";

        // WiX Burn bundles
        if (uninstallLower.Contains(@"package cache"))
            return "WiX Bundle";

        // Squirrel / Electron-based (Update.exe --uninstall)
        if (uninstallLower.Contains("update.exe") && uninstallLower.Contains("--uninstall"))
            return "Squirrel";

        // Click-once
        if (uninstallLower.Contains("rundll32.exe") && uninstallLower.Contains("dfshim"))
            return "ClickOnce";

        return "Unknown";
    }
}
