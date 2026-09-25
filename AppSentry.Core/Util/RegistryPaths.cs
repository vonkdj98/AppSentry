using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AppSentry.Core.Util;

/// <summary>
/// Registry helpers shared by the scanners and the UI: canonical uninstall paths,
/// opening a key from a stored KeyPath string, and reading a key's last-write time.
/// </summary>
public static class RegistryPaths
{
    public const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Opens a key from a stored path such as "HKLM\SOFTWARE\...", "HKU\S-1-5-21-...\Software\..."
    /// or the v1 form "HKCU\...". Always uses the 64-bit view so WOW6432Node paths resolve literally.
    /// </summary>
    public static RegistryKey? OpenReadOnly(string keyPath)
    {
        try
        {
            if (keyPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                return hklm.OpenSubKey(keyPath[5..], writable: false);
            }
            if (keyPath.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
                return Registry.CurrentUser.OpenSubKey(keyPath[5..], writable: false);
            if (keyPath.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase))
                return Registry.Users.OpenSubKey(keyPath[4..], writable: false);
        }
        catch (Exception)
        {
            // Access denied / hive not loaded.
        }
        return null;
    }

    /// <summary>Stringifies every value under a key (REG_MULTI_SZ joined with "; ", binary as hex).</summary>
    public static Dictionary<string, string> ReadAllValues(RegistryKey key)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames())
        {
            try
            {
                var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                values[name.Length == 0 ? "(Default)" : name] = Stringify(raw);
            }
            catch
            {
                // Unreadable value — skip it.
            }
        }
        return values;
    }

    public static string Stringify(object? raw) => raw switch
    {
        null => "",
        string s => s,
        string[] arr => string.Join("; ", arr),
        byte[] bytes => bytes.Length > 64 ? Convert.ToHexString(bytes, 0, 64) + "…" : Convert.ToHexString(bytes),
        _ => raw.ToString() ?? ""
    };

    /// <summary>Last write time of a key, or null if it can't be read.</summary>
    public static DateTime? GetLastWriteUtc(RegistryKey key)
    {
        try
        {
            var rc = RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long fileTime);
            return rc == 0 && fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues,
        IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor,
        out long lpftLastWriteTime);
}
