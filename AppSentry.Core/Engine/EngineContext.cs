using System.Security.Principal;
using Microsoft.Win32;

namespace AppSentry.Core.Engine;

/// <summary>Who the engine is running as, and which user profiles exist on the machine.</summary>
public sealed record EngineContext
{
    public EngineMode Mode { get; init; }
    public string UserSid { get; init; } = "";
    public string UserName { get; init; } = "";
    public bool IsElevated { get; init; }

    /// <summary>
    /// Baselines of access-dependent sources (services, tasks) are tied to this. If the engine
    /// later runs as a different user or elevation level, those sources re-baseline silently
    /// instead of reporting everything that became visible/invisible as added/removed.
    /// </summary>
    public string Fingerprint => $"{Mode}|{UserSid}|{(IsElevated ? "elevated" : "standard")}";

    public static EngineContext Capture(EngineMode mode)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return new EngineContext
        {
            Mode = mode,
            UserSid = identity.User?.Value ?? "",
            UserName = StripDomain(identity.Name),
            IsElevated = principal.IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem
        };
    }

    public string Describe() =>
        $"{Mode} mode as {UserName}{(IsElevated ? " (elevated)" : "")}";

    /// <summary>True for SIDs of real interactive accounts: local/AD (S-1-5-21) and Entra ID (S-1-12-1).</summary>
    public static bool IsUserSid(string sid) =>
        (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ||
         sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase)) &&
        !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every user profile on the machine (from ProfileList), whether or not its hive is loaded.</summary>
    public static List<UserProfile> GetUserProfiles()
    {
        var profiles = new List<UserProfile>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var list = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list == null) return profiles;
            foreach (var sid in list.GetSubKeyNames())
            {
                if (!IsUserSid(sid)) continue;
                using var key = list.OpenSubKey(sid);
                var path = key?.GetValue("ProfileImagePath") as string;
                if (string.IsNullOrWhiteSpace(path)) continue;
                profiles.Add(new UserProfile(sid, Environment.ExpandEnvironmentVariables(path), ResolveUserName(sid)));
            }
        }
        catch (Exception)
        {
            // Not fatal: callers fall back to the current user.
        }
        return profiles;
    }

    /// <summary>SID → "user" (domain stripped), falling back to the raw SID.</summary>
    public static string ResolveUserName(string sid)
    {
        try
        {
            if (new SecurityIdentifier(sid).Translate(typeof(NTAccount)) is NTAccount account)
                return StripDomain(account.Value);
        }
        catch (Exception) { }
        return sid;
    }

    /// <summary>SID → "DOMAIN\user", falling back to the raw SID.</summary>
    public static string ResolveAccountName(SecurityIdentifier sid)
    {
        try
        {
            if (sid.Translate(typeof(NTAccount)) is NTAccount account) return account.Value;
        }
        catch (Exception) { }
        return sid.Value;
    }

    public static string StripDomain(string name)
    {
        var slash = name.IndexOf('\\');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }
}

public sealed record UserProfile(string Sid, string ProfilePath, string UserName);
