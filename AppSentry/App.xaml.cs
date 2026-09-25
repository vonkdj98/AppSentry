using System.Diagnostics;
using System.Windows;
using AppSentry.Core.Backend;
using AppSentry.Core.Util;
using AppSentry.Services;
using AppSentry.ViewModels;
using AppSentry.Views;

namespace AppSentry;

/// <summary>
/// The tray app: Fluent theme, main window, tray icon and notifications. Lives until "Exit" —
/// closing the window only hides it (unless the user turned that off).
/// </summary>
public partial class App : Application
{
    private IMonitorBackend? _backend;
    private ShellViewModel? _shell;
    private MainWindow? _window;
    private TrayService? _tray;
    private bool _exiting;

    public ToastService? Toasts { get; private set; }

    public void ApplyTheme(string theme)
    {
        ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System
        };
    }

    public int RunTray(IMonitorBackend backend, bool startMinimized, EventWaitHandle? showSignal)
    {
        _backend = backend;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            EngineLog.Error("Unhandled UI exception", e.Exception);
            Dialogs.ShowError("AppSentry", $"Something went wrong: {e.Exception.Message}");
            e.Handled = true;
        };

        Startup += async (_, _) =>
        {
            var settings = UiSettingsStore.Load();
            ApplyTheme(settings.Theme);

            _shell = new ShellViewModel(backend, settings);
            _window = new MainWindow(_shell);
            _window.Closing += OnWindowClosing;

            Toasts = new ToastService(() => settings);
            Toasts.Activated += OnToastActivated;
            _tray = new TrayService(_shell, ShowWindow, id => { ShowWindow(); _shell.ShowEvent(id); }, ExitApp);

            _shell.NewEvents += events => { _ = Toasts.ShowAsync(events); _tray.Refresh(); };
            _shell.AttentionChanged += () => _tray.Refresh();

            if (showSignal != null) ListenForShowRequests(showSignal);
            if (!startMinimized) _window.Show();

            try
            {
                await _shell.InitializeAsync();
            }
            catch (Exception ex)
            {
                EngineLog.Error("Monitor failed to start", ex);
                Dialogs.ShowError("AppSentry", $"AppSentry couldn't start its monitor:\n\n{ex.Message}");
            }
        };

        return Run();
    }

    public void ShowWindow()
    {
        if (_window == null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Starts a new copy of the app once this one has exited (after switching service mode).</summary>
    public void Restart()
    {
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe, $"--after-exit {Environment.ProcessId}") { UseShellExecute = true });
        ExitApp();
    }

    public void ExitApp()
    {
        _exiting = true;
        _window?.SaveBounds();
        _tray?.Dispose();
        _window?.Close();
        _backend?.Dispose(); // lets an in-flight local scan commit first
        Shutdown();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _shell == null) return;
        _window!.SaveBounds();
        if (!_shell.Settings.CloseToTray)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(ExitApp);
            return;
        }

        e.Cancel = true;
        _window.Hide();
        if (!_shell.Settings.CloseToTrayTipShown)
        {
            _shell.Settings.CloseToTrayTipShown = true;
            _shell.SaveSettings();
            Toasts?.ShowInfo("AppSentry is still running", "It's in the notification area. Right-click the shield icon to exit.");
        }
    }

    private void OnToastActivated(string action, long? id)
    {
        if (_shell == null) return;
        ShowWindow();
        if (id is not { } eventId) return;
        _shell.ShowEvent(eventId);
        if (action == "exclude" && _shell.AllEvents.FirstOrDefault(e => e.Id == eventId) is { } ev)
            _ = _shell.ExcludeAsync(ev);
    }

    /// <summary>A second launch signals this event; bring the window up instead of starting twice.</summary>
    private void ListenForShowRequests(EventWaitHandle signal)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();
                Dispatcher.BeginInvoke(ShowWindow);
            }
        }) { IsBackground = true, Name = "AppSentry show requests" };
        thread.Start();
    }
}
