using System.ServiceProcess;
using AppSentry.Core.Backend;
using AppSentry.Core.Service;

namespace AppSentry;

internal static class Program
{
    private static readonly string MutexName = "AppSentry_SingleInstance_Mutex";

    /// <summary>
    /// AppSentry.exe                       tray app (connects to the service if it's running, else monitors in-process)
    /// AppSentry.exe --service             run as the Windows service (started by the Service Control Manager)
    /// AppSentry.exe --install-service     copy to Program Files, register and start the service (UAC prompt)
    /// AppSentry.exe --uninstall-service   stop and remove the service; history is kept
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
        {
            ServiceBase.Run(new AppSentryWindowsService());
            return 0;
        }
        if (args.Contains("--install-service", StringComparer.OrdinalIgnoreCase))
            return RunServiceCommand("--install-service", ServiceInstaller.Install);
        if (args.Contains("--uninstall-service", StringComparer.OrdinalIgnoreCase))
            return RunServiceCommand("--uninstall-service", ServiceInstaller.Uninstall);

        // Restarting after a service install/uninstall: wait for the old window to exit first.
        var afterExit = Array.FindIndex(args, a => a.Equals("--after-exit", StringComparison.OrdinalIgnoreCase));
        if (afterExit >= 0 && afterExit + 1 < args.Length && int.TryParse(args[afterExit + 1], out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15_000); }
            catch (ArgumentException) { } // already gone
        }

        // --data-dir <path>: run the in-process engine against another folder (testing, portable use).
        // One instance per data folder, so it can run beside the normal install.
        var dataDirIndex = Array.FindIndex(args, a => a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase));
        var dataDir = dataDirIndex >= 0 && dataDirIndex + 1 < args.Length ? Path.GetFullPath(args[dataDirIndex + 1]) : null;
        var mutexName = dataDir == null
            ? MutexName
            : $"{MutexName}_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16]}";

        // Prevent multiple instances
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "AppSentry is already running.",
                "AppSentry",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        // Prefer the machine-wide service; fall back to monitoring in this process.
        IMonitorBackend backend = dataDir != null
            ? new LocalBackend(dataDir)
            : PipeBackend.TryConnect(TimeSpan.FromMilliseconds(700)) ?? (IMonitorBackend)new LocalBackend(LocalBackend.DefaultDataDir);
        Application.Run(new MainForm(backend));
        return 0;
    }

    private static int RunServiceCommand(string argument, Func<Action<string>, int> command)
    {
        if (!ServiceInstaller.IsElevated())
            return ServiceInstaller.RunElevated(argument);

        var lines = new List<string>();
        var code = command(lines.Add);
        MessageBox.Show(string.Join(Environment.NewLine, lines), "AppSentry service",
            MessageBoxButtons.OK, code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return code;
    }
}
