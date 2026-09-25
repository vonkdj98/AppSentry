using System.Security.AccessControl;
using System.Security.Principal;

namespace AppSentry.Core.Service;

/// <summary>
/// %ProgramData%\AppSentry — the service's database and log. Locked to SYSTEM and
/// Administrators (history names users and machines); tray apps read it through the pipe.
/// </summary>
public static class ServiceDataDir
{
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
        if (!dir.Exists) dir.Create(security);
        else dir.SetAccessControl(security);
        return Path;
    }
}
