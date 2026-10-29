using System.Security;
using System.Text;
using AppSentry.Core.Util;
using Microsoft.Win32;

namespace AppSentry.Core.Service;

/// <summary>
/// Starts the tray app when anyone signs in, through a Task Scheduler task the installer registers. The per-user Run
/// key it replaces was skipped by Explorer at sign-in on a managed PC with nothing logged to say why, which left the
/// app off after every restart; Task Scheduler doesn't go through Explorer. Each user can still opt out (the app
/// exits at once when started this way with "Start with Windows" off).
/// </summary>
public static class TrayAutostart
{
    public const string TaskName = "AppSentry Tray";
    public const string Argument = "--autostart";

    // Users can't read an administrator's task, so the app learns it exists from this (readable by everyone).
    private const string MarkerKey = @"SOFTWARE\AppSentry";
    private const string MarkerValue = "TrayAutostartTask";

    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MarkerKey);
                return key?.GetValue(MarkerValue) is int and not 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Must run elevated.</summary>
    public static int Register(Action<string> log)
    {
        if (!ServiceInstaller.IsElevated()) { log("Administrator rights are required."); return 5; }
        var exe = Path.Combine(ServiceInstaller.InstallDir, "AppSentry.exe");
        // Written where only administrators can write, so nobody can swap the file before schtasks reads it.
        var xmlPath = Path.Combine(ServiceInstaller.InstallDir, "autostart-task.xml");
        try
        {
            File.WriteAllText(xmlPath, TaskXml(exe), Encoding.Unicode);
            var create = Schtasks($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f");
            if (!create.Succeeded) { log($"schtasks failed: {create.StdOut}{create.StdErr}"); return create.ExitCode ?? 1; }
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (IOException) { }
        }
        using (var key = Registry.LocalMachine.CreateSubKey(MarkerKey))
            key.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
        log("AppSentry will start for each user who signs in.");
        return 0;
    }

    /// <summary>Must run elevated.</summary>
    public static int Unregister(Action<string> log)
    {
        if (!ServiceInstaller.IsElevated()) { log("Administrator rights are required."); return 5; }
        Schtasks($"/delete /tn \"{TaskName}\" /f");
        using (var key = Registry.LocalMachine.OpenSubKey(MarkerKey, writable: true))
            key?.DeleteValue(MarkerValue, throwOnMissingValue: false);
        log("AppSentry no longer starts at sign-in.");
        return 0;
    }

    /// <summary>
    /// Any member of Users, in their own session, without elevation. No time limit (the default stops a task after 3
    /// days), normal priority (the default is below normal), and parallel instances so a second user's sign-in isn't
    /// ignored while the first user's copy runs.
    /// </summary>
    internal static string TaskXml(string exe) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>AppSentry</Author>
            <Description>Starts the AppSentry tray app when someone signs in.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <Delay>PT5S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Users">
              <GroupId>S-1-5-32-545</GroupId>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Actions Context="Users">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
              <Arguments>{Argument}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private static ProcessRunner.Result Schtasks(string arguments)
    {
        var result = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments, TimeSpan.FromSeconds(60));
        EngineLog.Info($"schtasks.exe {arguments} → {result.ExitCode}");
        return result;
    }
}
