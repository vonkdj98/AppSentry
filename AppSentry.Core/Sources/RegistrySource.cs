using AppSentry.Core.Engine;
using AppSentry.Core.Util;
using AppSentry.Models;
using Microsoft.Win32;

namespace AppSentry.Core.Sources;

/// <summary>
/// Reads Add/Remove Programs entries from:
///   - HKLM 64-bit and 32-bit (WOW6432Node) uninstall keys — scopes HKLM64 / HKLM32
///   - every loaded user hive under HKU (local/AD and Entra ID accounts) — scope HKU\{sid}
///
/// A user hive that isn't loaded (user signed out) or can't be read (not elevated) is not
/// reported as a completed scope, so its apps are carried forward rather than "removed".
/// </summary>
public static class RegistrySource
{
    public static void Scan(EngineContext context, InventoryResult result)
    {
        var problems = new List<string>();

        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            ReadScope(hklm, RegistryPaths.Uninstall, $@"HKLM\{RegistryPaths.Uninstall}", Scopes.Machine64, "All users", result, problems);
            ReadScope(hklm, RegistryPaths.Uninstall32, $@"HKLM\{RegistryPaths.Uninstall32}", Scopes.Machine32, "All users", result, problems);
        }

        string[] loadedSids;
        try
        {
            loadedSids = Registry.Users.GetSubKeyNames().Where(EngineContext.IsUserSid).ToArray();
        }
        catch (Exception ex)
        {
            loadedSids = [];
            problems.Add($"HKU not readable ({ex.Message})");
        }

        foreach (var sid in loadedSids)
        {
            // A standard user can only read their own hive; don't even try the others.
            var isSelf = sid.Equals(context.UserSid, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !context.IsElevated) continue;

            var userName = EngineContext.ResolveUserName(sid);
            ReadScope(Registry.Users, $@"{sid}\{RegistryPaths.Uninstall}", $@"HKU\{sid}\{RegistryPaths.Uninstall}",
                Scopes.User(sid), userName, result, problems);
        }

        result.Health["Registry"] = problems.Count == 0 ? "ok" : "partial: " + string.Join("; ", problems);
    }

    private static void ReadScope(RegistryKey hive, string subPath, string keyPrefix, string scope,
        string installedFor, InventoryResult result, List<string> problems)
    {
        RegistryKey? root;
        try
        {
            root = hive.OpenSubKey(subPath, writable: false);
        }
        catch (Exception ex)
        {
            problems.Add($"{scope} unreadable ({ex.GetType().Name})");
            return; // scope NOT completed → previous entries are kept
        }

        if (root == null)
        {
            // The key simply doesn't exist (common for users with no per-user installs): that's a
            // complete, empty scope, not an error.
            result.CompletedScopes.Add(scope);
            return;
        }

        using (root)
        {
            string[] names;
            try { names = root.GetSubKeyNames(); }
            catch (Exception ex)
            {
                problems.Add($"{scope} unreadable ({ex.GetType().Name})");
                return;
            }

            foreach (var subKeyName in names)
            {
                try
                {
                    using var key = root.OpenSubKey(subKeyName, writable: false);
                    if (key == null) continue;
                    var app = ReadEntry(key, subKeyName, keyPrefix, scope, installedFor);
                    if (app != null) result.Add(app);
                }
                catch (Exception)
                {
                    // One unreadable entry doesn't invalidate the scope.
                }
            }
        }
        result.CompletedScopes.Add(scope);
    }

    private static InstalledApp? ReadEntry(RegistryKey key, string subKeyName, string keyPrefix, string scope, string installedFor)
    {
        var name = Str(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Hidden components and child patch entries aren't apps in Add/Remove Programs.
        if (key.GetValue("SystemComponent") is int sc && sc == 1) return null;
        if (!string.IsNullOrEmpty(Str(key, "ParentKeyName"))) return null;
        var releaseType = Str(key, "ReleaseType");
        if (releaseType is "Update" or "Hotfix" or "Security Update") return null;

        var uninstall = Str(key, "UninstallString");
        var quietUninstall = Str(key, "QuietUninstallString");
        var installSource = Str(key, "InstallSource");
        var isMsi = key.GetValue("WindowsInstaller") is int wi && wi == 1;
        var productCode = Guid.TryParse(subKeyName, out _) && (isMsi || uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
            ? subKeyName.ToUpperInvariant()
            : "";

        long? sizeKb = key.GetValue("EstimatedSize") switch
        {
            int i when i > 0 => i,
            long l when l > 0 => l,
            _ => null
        };

        return new InstalledApp
        {
            KeyPath = $@"{keyPrefix}\{subKeyName}",
            Scope = scope,
            Name = name,
            Version = Str(key, "DisplayVersion"),
            Publisher = Str(key, "Publisher"),
            InstallDate = Str(key, "InstallDate"),
            InstallLocation = Str(key, "InstallLocation"),
            InstallSource = installSource,
            InstallType = DetectInstallType(key, uninstall, quietUninstall, isMsi, subKeyName),
            InstalledFor = installedFor,
            UninstallString = uninstall,
            QuietUninstallString = quietUninstall,
            ProductCode = productCode,
            EstimatedSizeKb = sizeKb,
            KeyLastWriteUtc = RegistryPaths.GetLastWriteUtc(key),
            RawValues = RegistryPaths.ReadAllValues(key)
        };
    }

    private static string Str(RegistryKey key, string name) =>
        RegistryPaths.Stringify(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)).Trim();

    /// <summary>Installer technology from registry clues.</summary>
    private static string DetectInstallType(RegistryKey key, string uninstall, string quietUninstall, bool isMsi, string subKeyName)
    {
        var u = uninstall.ToLowerInvariant();
        var q = quietUninstall.ToLowerInvariant();

        if (key.GetValue("BundleVersion") != null || key.GetValue("BundleCachePath") != null || u.Contains(@"package cache"))
            return "WiX Bundle";
        if (isMsi || u.Contains("msiexec")) return "MSI";
        if ((subKeyName.Contains('_') && subKeyName.Contains('!')) || u.Contains("windowsapps")) return "Store";
        if (u.Contains("unins00") || key.GetValue("Inno Setup: Setup Version") != null) return "InnoSetup";
        if (u.Contains("update.exe") && (u.Contains("--uninstall") || q.Contains("--uninstall"))) return "Squirrel";
        if (u.Contains("rundll32") && u.Contains("dfshim")) return "ClickOnce";
        if (u.Contains("installshield") || u.Contains(@"\installshield installation information\")) return "InstallShield";
        if (u.Contains("uninstall.exe") || u.Contains("uninst.exe")) return "NSIS";
        return "Unknown";
    }
}
