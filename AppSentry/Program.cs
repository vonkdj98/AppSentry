using System.IO;
using System.ServiceProcess;
using System.Windows;
using AppSentry.Core.Backend;
using AppSentry.Core.Service;
using AppSentry.Demo;
using AppSentry.Services;

namespace AppSentry;

internal static class Program
{
    private const string MutexName = "AppSentry_SingleInstance_Mutex";

    /// <summary>
    /// AppSentry.exe                       tray app (connects to the service if it's running, else monitors in-process)
    /// AppSentry.exe --minimized           same, starting hidden in the tray (used by "Start with Windows")
    /// AppSentry.exe --service             run as the Windows service (started by the Service Control Manager)
    /// AppSentry.exe --install-service     copy to Program Files, register and start the service (UAC prompt)
    /// AppSentry.exe --uninstall-service   stop and remove the service; history is kept
    /// AppSentry.exe --data-dir &lt;path&gt;     monitor in-process against another folder (testing, portable use)
    /// AppSentry.exe --demo [--synthetic]  sample data, no monitoring (design review); --synthetic = no data from this PC
    /// AppSentry.exe --screenshots &lt;dir&gt;   render every page with sample data to PNGs, then exit (--theme, --synthetic)
    /// AppSentry.exe --export-icon &lt;path&gt;  write the app icon (.ico)
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (Has(args, "--service"))
        {
            ServiceBase.Run(new AppSentryWindowsService());
            return 0;
        }
        if (Has(args, "--install-service")) return RunServiceCommand("--install-service", ServiceInstaller.Install);
        if (Has(args, "--uninstall-service")) return RunServiceCommand("--uninstall-service", ServiceInstaller.Uninstall);
        if (Value(args, "--export-icon") is { } iconPath)
        {
            BrandIcon.WriteIco(Path.GetFullPath(iconPath));
            return 0;
        }
        if (Value(args, "--screenshots") is { } shotDir)
        {
            UiSettingsStore.InMemoryOnly = true;
            return ScreenshotRunner.Run(Path.GetFullPath(shotDir), Value(args, "--theme") ?? "Light", Has(args, "--synthetic"));
        }

        // Restarting after a service install/uninstall: wait for the old window to exit first.
        if (Value(args, "--after-exit") is { } pidText && int.TryParse(pidText, out var pid))
        {
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15_000); }
            catch (ArgumentException) { } // already gone
        }

        var demo = Has(args, "--demo");
        UiSettingsStore.InMemoryOnly = demo;
        var dataDir = Value(args, "--data-dir") is { } d ? Path.GetFullPath(d) : null;
        var instance = demo ? "demo" : dataDir?.ToLowerInvariant() ?? "";
        var suffix = instance.Length == 0
            ? ""
            : "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instance)))[..16];

        // One instance per data folder. A second launch just brings the running window forward.
        using var mutex = new Mutex(initiallyOwned: true, MutexName + suffix, out var createdNew);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "AppSentry_Show" + suffix);
        if (!createdNew)
        {
            showSignal.Set();
            return 0;
        }

        IMonitorBackend backend = demo
            ? new DemoBackend(Has(args, "--synthetic"))
            : dataDir != null
                ? new LocalBackend(dataDir)
                : PipeBackend.TryConnect(TimeSpan.FromMilliseconds(700)) ?? (IMonitorBackend)new LocalBackend(LocalBackend.DefaultDataDir);

        var app = new App();
        app.InitializeComponent();
        return app.RunTray(backend, startMinimized: Has(args, "--minimized"), showSignal);
    }

    private static bool Has(string[] args, string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private static string? Value(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int RunServiceCommand(string argument, Func<Action<string>, int> command)
    {
        if (!ServiceInstaller.IsElevated())
            return ServiceInstaller.RunElevated(argument);

        var lines = new List<string>();
        var code = command(lines.Add);
        MessageBox.Show(string.Join(Environment.NewLine, lines), "AppSentry service",
            MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return code;
    }
}
