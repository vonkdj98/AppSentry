using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using AppSentry.Core.Util;

namespace AppSentry.Core.Service;

/// <summary>
/// Installs/removes the AppSentry service. The app folder is copied to %ProgramFiles%\AppSentry
/// first so the service never runs from a Downloads folder or a network share.
/// </summary>
public static class ServiceInstaller
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AppSentry");

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var sc = new ServiceController(AppSentryWindowsService.Name);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // not installed
        }
    }

    /// <summary>Re-runs this exe elevated with <paramref name="argument"/> and returns its exit code (UAC prompt).</summary>
    public static int RunElevated(string argument)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate AppSentry.exe");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, argument) { UseShellExecute = true, Verb = "runas" });
            proc!.WaitForExit();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 1223; // ERROR_CANCELLED: the user declined the UAC prompt
        }
    }

    /// <summary>Must run elevated. Returns a process exit code; <paramref name="log"/> receives progress lines.</summary>
    public static int Install(Action<string> log)
    {
        if (!IsElevated()) { log("Administrator rights are required."); return 5; }

        var sourceDir = AppContext.BaseDirectory.TrimEnd('\\');
        var targetDir = InstallDir;
        var targetExe = Path.Combine(targetDir, "AppSentry.exe");

        if (GetStatus() is { } status)
        {
            if (status != ServiceControllerStatus.Stopped)
            {
                log("Stopping the running service…");
                Sc("stop AppSentry");
                WaitFor(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            }
        }

        if (!sourceDir.Equals(targetDir, StringComparison.OrdinalIgnoreCase))
        {
            log($"Copying AppSentry to {targetDir}…");
            try
            {
                // The official build is one self-contained exe: copy just that, so nothing that happens to sit beside
                // it (a DLL planted in Downloads) ends up in Program Files, loaded by SYSTEM. A development build
                // needs its whole folder.
#pragma warning disable IL3000 // an empty Location is exactly how a single-file app is recognized
                var singleFile = string.IsNullOrEmpty(typeof(ServiceInstaller).Assembly.Location);
#pragma warning restore IL3000
                CopyDirectory(sourceDir, targetDir, log, singleFile ? [Path.GetFileName(Environment.ProcessPath!)] : null);
            }
            catch (IOException ex)
            {
                log($"Could not copy files ({ex.Message}). Close any AppSentry window started from {targetDir} and try again.");
                return 32; // ERROR_SHARING_VIOLATION
            }
        }

        var binPath = $"\"\\\"{targetExe}\\\" --service\"";
        var exists = GetStatus() != null;
        var create = exists
            ? Sc($"config AppSentry binPath= {binPath} start= delayed-auto DisplayName= \"AppSentry Monitor\"")
            : Sc($"create AppSentry binPath= {binPath} start= delayed-auto DisplayName= \"AppSentry Monitor\"");
        if (!create.Succeeded) { log($"sc.exe failed: {create.StdOut}{create.StdErr}"); return create.ExitCode ?? 1; }

        try { SetExtractionDir(targetDir); }
        catch (Exception ex) { log($"Warning: could not set the service's runtime folder: {ex.Message}"); }

        Sc("description AppSentry \"Monitors software installs, updates, removals, services and scheduled tasks.\"");
        Sc("failure AppSentry reset= 86400 actions= restart/60000/restart/60000/restart/300000");

        try { WindowsEventLogWriter.EnsureSource(); }
        catch (Exception ex) { log($"Warning: could not register the event log source: {ex.Message}"); }

        log("Starting the service…");
        var start = Sc("start AppSentry");
        if (!start.Succeeded && !start.StdOut.Contains("1056")) // 1056 = already running
        {
            log($"The service was installed but did not start: {start.StdOut}{start.StdErr}");
            return start.ExitCode ?? 1;
        }
        WaitFor(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        log("AppSentry service installed and running.");
        return 0;
    }

    public static int Uninstall(Action<string> log)
    {
        if (!IsElevated()) { log("Administrator rights are required."); return 5; }
        if (GetStatus() == null) { log("The AppSentry service is not installed."); return 0; }

        Sc("stop AppSentry");
        WaitFor(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
        var delete = Sc("delete AppSentry");
        if (!delete.Succeeded) { log($"sc.exe failed: {delete.StdOut}{delete.StdErr}"); return delete.ExitCode ?? 1; }
        log($"AppSentry service removed. Its history is kept in {ServiceDataDir.Path}; the program files are in {InstallDir}.");
        return 0;
    }

    /// <summary>
    /// The single-file exe unpacks its native DLLs on start, by default under the account's %TEMP%: for SYSTEM that's
    /// C:\Windows\Temp, where any user can create folders, and a DLL planted in the predictable folder would be loaded
    /// by the service. The service unpacks into a folder under the install directory instead, which only
    /// administrators can write.
    /// </summary>
    private static void SetExtractionDir(string installDir)
    {
        var dir = Path.Combine(installDir, "runtime");
        Directory.CreateDirectory(dir);
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{AppSentryWindowsService.Name}", writable: true)
                        ?? throw new InvalidOperationException("the service's registry key is missing");
        key.SetValue("Environment", new[] { $"DOTNET_BUNDLE_EXTRACT_BASE_DIR={dir}" }, Microsoft.Win32.RegistryValueKind.MultiString);
    }

    private static ProcessRunner.Result Sc(string arguments)
    {
        // By full path: the working directory or PATH must never supply it.
        var result = ProcessRunner.Run(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments, TimeSpan.FromSeconds(60));
        EngineLog.Info($"sc.exe {arguments} → {result.ExitCode}");
        return result;
    }

    private static void WaitFor(ServiceControllerStatus status, TimeSpan timeout)
    {
        try
        {
            using var sc = new ServiceController(AppSentryWindowsService.Name);
            sc.WaitForStatus(status, timeout);
        }
        catch (Exception)
        {
            // Timed out or service gone; the caller reports what sc.exe said.
        }
    }

    /// <summary>
    /// How long a locked file is waited for: a program that has only just stopped (the service finishing, or antivirus
    /// scanning a freshly closed 70 MB exe) can keep its file open for several seconds.
    /// </summary>
    internal static int CopyAttempts = 30;
    internal static TimeSpan CopyRetryDelay = TimeSpan.FromSeconds(2);

    internal static void CopyDirectory(string source, string target, Action<string>? log = null, IReadOnlyCollection<string>? only = null)
    {
        Directory.CreateDirectory(target);
        var files = only != null ? only.Select(name => Path.Combine(source, name)) : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(file, destination, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < CopyAttempts)
                {
                    if (attempt == 1) log?.Invoke($"{Path.GetFileName(destination)} is still in use; waiting for it to be released…");
                    Thread.Sleep(CopyRetryDelay);
                }
            }
        }
    }
}
