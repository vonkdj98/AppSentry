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

        // Prevent multiple instances
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, MutexName, out bool createdNew);
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
        IMonitorBackend backend = PipeBackend.TryConnect(TimeSpan.FromMilliseconds(700))
                                  ?? (IMonitorBackend)new LocalBackend(LocalBackend.DefaultDataDir);
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
