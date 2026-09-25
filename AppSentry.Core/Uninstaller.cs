using System.Text.RegularExpressions;
using AppSentry.Core.Sources;
using AppSentry.Core.Util;
using AppSentry.Models;
using Windows.Management.Deployment;

namespace AppSentry.Core;

/// <summary>How to uninstall one app. <see cref="IsStore"/> plans are executed with <see cref="Uninstaller.RemoveStorePackageAsync"/>.</summary>
public sealed record UninstallPlan(string FileName, string Arguments, bool Elevate, bool IsStore, string PackageFullName)
{
    public string Display => IsStore ? $"Remove Store package {PackageFullName}" : $"{FileName} {Arguments}".Trim();
}

/// <summary>
/// Builds uninstall commands without v1's problems:
///  - no "cmd /c &lt;string&gt;" wrapper (broke on paths with &amp;, parentheses or quotes);
///  - MSI entries use msiexec /x — many UninstallStrings are "/I{GUID}", which opens repair/modify;
///  - elevation only for machine-wide installs (elevating a per-user uninstaller can target the
///    admin's profile instead of the user's);
///  - Store packages are removed through the PackageManager API.
/// </summary>
public static partial class Uninstaller
{
    [GeneratedRegex(@"msiexec(\.exe)?\s+/[IX]\s*(?<code>\{[0-9A-F\-]{36}\})", RegexOptions.IgnoreCase)]
    private static partial Regex MsiCommand();

    public static UninstallPlan? Plan(InstalledApp app)
    {
        if (Scopes.SourceOf(app.Scope) == DetectionSource.Store || app.PackageFullName.Length > 0)
            return app.PackageFullName.Length > 0 ? new UninstallPlan("", "", false, true, app.PackageFullName) : null;

        var machineWide = app.Scope is Scopes.Machine64 or Scopes.Machine32 || app.KeyPath.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);

        // Prefer what the registry says now; the stored copy may be from before an update.
        var uninstall = app.UninstallString;
        using (var key = RegistryPaths.OpenReadOnly(app.KeyPath))
        {
            if (key?.GetValue("UninstallString") is string live && live.Length > 0) uninstall = live;
        }

        var msi = MsiCommand().Match(uninstall);
        var productCode = msi.Success ? msi.Groups["code"].Value : app.ProductCode;
        if (productCode.Length > 0 && (msi.Success || uninstall.Length == 0))
            return new UninstallPlan("msiexec.exe", $"/x {productCode}", machineWide, false, "");

        if (uninstall.Length == 0) return null;

        var exe = FolderOwnership.ExecutableOf(uninstall);
        var args = uninstall.Trim();
        if (args.StartsWith('"')) args = args[(args.IndexOf('"', 1) + 1)..];
        else if (args.StartsWith(exe, StringComparison.OrdinalIgnoreCase)) args = args[exe.Length..];
        return new UninstallPlan(exe, args.Trim(), machineWide, false, "");
    }

    /// <summary>Removes a Store package for the current user. Returns null on success, else the error text.</summary>
    public static async Task<string?> RemoveStorePackageAsync(string packageFullName)
    {
        try
        {
            var result = await new PackageManager().RemovePackageAsync(packageFullName).AsTask().ConfigureAwait(false);
            return result.ExtendedErrorCode is { HResult: not 0 } ? result.ErrorText : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
