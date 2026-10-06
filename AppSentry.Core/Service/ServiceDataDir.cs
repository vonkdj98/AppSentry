using System.Security.AccessControl;
using System.Security.Principal;
using AppSentry.Core.Util;

namespace AppSentry.Core.Service;

/// <summary>
/// %ProgramData%\AppSentry — the service's database and log. Locked to SYSTEM and
/// Administrators (history names users and machines); tray apps read it through the pipe.
/// </summary>
public static class ServiceDataDir
{
    /// <summary>A real folder owned by SYSTEM, Administrators or TrustedInstaller.</summary>
    internal static bool Trusted(DirectoryInfo dir)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var owner = dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner != null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                                 owner.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"); // TrustedInstaller
    }

    public static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppSentry");

    public static string Ensure()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var dir = new DirectoryInfo(Path);
        if (dir.Exists && !Trusted(dir))
        {
            // Created before the service by someone else (any user can create folders in %ProgramData%), or a link
            // elsewhere: its files and ACL aren't ours to trust. Moved aside, not deleted.
            var aside = Path + ".untrusted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            Directory.Move(Path, aside);
            EngineLog.Warn($"{Path} wasn't created by SYSTEM or an administrator (or is a link); moved it to {aside}");
            dir = new DirectoryInfo(Path);
        }
        if (!dir.Exists) dir.Create(security);
        else dir.SetAccessControl(security);
        return Path;
    }
}
