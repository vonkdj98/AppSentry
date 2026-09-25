using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AppSentry.Core.Engine;
using AppSentry.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AppSentry.Core.Sources;

/// <summary>
/// Microsoft Store / MSIX packages via the WinRT PackageManager API — no PowerShell process.
///
/// Packages are keyed by <b>family name</b> (publisher + name, no version), so a Store update
/// is one Updated event instead of the v1 Removed + Installed pair (v1 keyed on the full name,
/// which contains the version).
/// </summary>
public static partial class StoreSource
{
    // DisplayName resolution reads each package's resources; cache it across scans.
    private static readonly ConcurrentDictionary<string, (string Name, string Publisher)> DisplayCache = new(StringComparer.OrdinalIgnoreCase);

    public static void Scan(EngineContext context, InventoryResult result)
    {
        PackageManager manager;
        try
        {
            manager = new PackageManager();
        }
        catch (Exception ex)
        {
            result.Health["Store"] = $"failed: {ex.Message}";
            return;
        }

        // Standalone: the current user's packages (no admin needed).
        // Service: every profile on the machine (LocalSystem may query any user).
        var targets = context.Mode == EngineMode.Service
            ? EngineContext.GetUserProfiles().Select(p => (p.Sid, p.UserName, QueryArg: p.Sid)).ToList()
            : [(context.UserSid, context.UserName, QueryArg: "")];

        var failures = new List<string>();
        foreach (var (sid, userName, queryArg) in targets)
        {
            var scope = Scopes.Store(sid);
            try
            {
                foreach (var package in manager.FindPackagesForUser(queryArg))
                {
                    var app = TryRead(package, sid, userName, scope);
                    if (app != null) result.Add(app);
                }
                result.CompletedScopes.Add(scope);
            }
            catch (Exception ex)
            {
                failures.Add($"{userName}: {ex.Message}");
            }
        }

        result.Health["Store"] = failures.Count == 0 ? "ok" : "partial: " + string.Join("; ", failures);
    }

    private static InstalledApp? TryRead(Package package, string sid, string userName, string scope)
    {
        try
        {
            if (package.IsFramework || package.IsResourcePackage) return null;
            if (package.SignatureKind == PackageSignatureKind.System) return null; // OS components

            var id = package.Id;
            if (IsSystemPackage(id.Name)) return null;

            var (displayName, publisherName) = DisplayCache.GetOrAdd(id.FullName, _ =>
            {
                var dn = Try(() => package.DisplayName);
                var pn = Try(() => package.PublisherDisplayName);
                return (
                    string.IsNullOrWhiteSpace(dn) || dn.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? CleanName(id.Name) : dn.Trim(),
                    string.IsNullOrWhiteSpace(pn) || pn.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? CleanPublisher(id.Publisher) : pn.Trim());
            });

            var v = id.Version;
            var version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            string? location = null;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                try { location = package.InstalledPath; } catch { }
            }
            location ??= Try(() => package.InstalledLocation?.Path) ?? "";
            DateTime? installed = null;
            try { installed = package.InstalledDate.UtcDateTime; } catch { }

            var signature = package.SignatureKind switch
            {
                PackageSignatureKind.Store => "Microsoft Store",
                PackageSignatureKind.Developer => "Sideloaded (developer)",
                PackageSignatureKind.Enterprise => "Sideloaded (enterprise)",
                _ => "Unsigned"
            };

            return new InstalledApp
            {
                KeyPath = $@"STORE\{sid}\{id.FamilyName}",
                Scope = scope,
                Name = displayName,
                Version = version,
                Publisher = publisherName,
                InstallDate = installed?.ToLocalTime().ToString("yyyyMMdd") ?? "",
                InstallLocation = location,
                InstallSource = signature,
                InstallType = "Store",
                InstalledFor = userName,
                PackageFullName = id.FullName,
                PackageFamilyName = id.FamilyName,
                KeyLastWriteUtc = installed,
                RawValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PackageFullName"] = id.FullName,
                    ["PackageFamilyName"] = id.FamilyName,
                    ["Version"] = version,
                    ["Publisher"] = id.Publisher,
                    ["Architecture"] = id.Architecture.ToString(),
                    ["SignatureKind"] = package.SignatureKind.ToString(),
                    ["InstalledPath"] = location,
                    ["IsBundle"] = package.IsBundle.ToString(),
                    ["IsDevelopmentMode"] = package.IsDevelopmentMode.ToString()
                }
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Try(Func<string?> read)
    {
        try { return read(); } catch { return null; }
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex PascalBoundary();

    /// <summary>"Microsoft.WindowsCalculator" → "Windows Calculator" (fallback when DisplayName is unavailable).</summary>
    private static string CleanName(string packageName)
    {
        var name = packageName;
        foreach (var prefix in new[] { "Microsoft.Windows.", "MicrosoftWindows.", "Microsoft.", "Windows." })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }
        var readable = PascalBoundary().Replace(name, " ").Replace("_", " ").Replace(".", " ").Trim();
        return string.IsNullOrWhiteSpace(readable) ? packageName : readable;
    }

    /// <summary>"CN=Microsoft Corporation, O=…" → "Microsoft Corporation".</summary>
    private static string CleanPublisher(string publisher)
    {
        if (!publisher.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return publisher.Trim();
        var rest = publisher[3..];
        var comma = rest.IndexOf(',');
        return (comma >= 0 ? rest[..comma] : rest).Trim().Trim('"');
    }

    /// <summary>Store-signed packages that are really plumbing, not apps.</summary>
    private static bool IsSystemPackage(string packageName)
    {
        var lower = packageName.ToLowerInvariant();
        string[] prefixes =
        [
            "microsoft.net.", "microsoft.vclibs", "microsoft.ui.xaml", "microsoft.directx", "microsoft.services.",
            "microsoft.advertising", "microsoft.windowsappruntime", "microsoft.winappruntime", "microsoft.aad.brokerplugin",
            "microsoft.accountscontrol", "microsoft.lockapp", "microsoft.ecapp", "microsoft.creddialoghost",
            "microsoft.bioenrollment", "microsoft.windows.cloudexperiencehost", "microsoft.windows.contentdeliverymanager",
            "microsoft.windows.oobenetworkconnectionflow", "microsoft.windows.parentalcontrols", "microsoft.windows.capturepicker",
            "microsoft.windows.pinningconfirmationdialog", "microsoft.windows.secureassessmentbrowser",
            "microsoft.windows.search", "microsoft.windows.appresolverux", "microsoft.windows.assignedaccesslockapp",
            "microsoft.windows.startmenuexperiencehost", "microsoft.windows.shellexperiencehost",
            "windows.cbspreview", "windows.immersivecontrolpanel", "windows.printdialog", "inputapp", "narratorquickstart",
            // winget's package index: updates several times a day and was 79% of one v1 history (6,375 of 8,095 events)
            "microsoft.winget.source"
        ];
        return prefixes.Any(lower.StartsWith);
    }
}
