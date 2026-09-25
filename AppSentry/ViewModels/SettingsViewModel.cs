using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Input;
using AppSentry.Core.Backend;
using AppSentry.Core.Service;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

/// <summary>
/// Settings page. Monitoring settings live in the engine (shared by every user of the service);
/// notification, appearance and startup preferences are per user.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;
    private EngineSettings _engine = new();
    private bool _loaded;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        IntervalOptions =
        [
            new("Every minute", 1),
            new("Every 5 minutes", 5),
            new("Every 10 minutes", 10),
            new("Every 30 minutes", 30),
            new("Off (real-time only)", 0)
        ];
        ThemeOptions = ["System", "Light", "Dark"];
        RetentionOptions =
        [
            new("Keep everything", 0),
            new("2 years", 730),
            new("1 year", 365),
            new("6 months", 182),
            new("3 months", 91),
            new("1 month", 30)
        ];

        ServiceCommand = new AsyncCommand(ServiceActionAsync);
        ResumeCommand = new RelayCommand(() => _shell.PauseNotifications(null));
        TestNotificationCommand = new RelayCommand(() => ((App)Application.Current).Toasts?.ShowTest());
        ExportCommand = new RelayCommand(Export, () => _shell.AllEvents.Count > 0);
        OpenDataFolderCommand = new RelayCommand(() => Dialogs.OpenFolder(DataDir), () => Directory.Exists(DataDir));
        OpenLogCommand = new RelayCommand(() => Dialogs.OpenFolder(Path.Combine(DataDir, "engine.log")), () => File.Exists(Path.Combine(DataDir, "engine.log")));
        ClearHistoryCommand = new AsyncCommand(ClearHistoryAsync, () => _shell.CanModify && _shell.AllEvents.Count > 0);
    }

    public void OnNavigatedTo() => OnPropertiesChanged(nameof(ServiceStatusText), nameof(ServiceActionText), nameof(StartWithWindows), nameof(PausedText), nameof(IsPaused));

    public async Task LoadAsync()
    {
        _engine = await _shell.Backend.GetSettingsAsync();
        _loaded = true;
        OnPropertiesChanged(nameof(RealtimeEnabled), nameof(SelectedInterval), nameof(SelectedRetention), nameof(CanModify));
        _shell.OnRealtimeChanged();
    }

    public bool CanModify => _shell.CanModify;

    // ── Monitoring (engine) ──────────────────────────────────────────────────

    public IReadOnlyList<Option<int>> IntervalOptions { get; }

    public bool RealtimeEnabled
    {
        get => _engine.RealtimeEnabled;
        set
        {
            if (value == _engine.RealtimeEnabled) return;
            _ = SaveEngineAsync(_engine with { RealtimeEnabled = value });
        }
    }

    public Option<int> SelectedInterval
    {
        get => IntervalOptions.FirstOrDefault(o => o.Value == _engine.ScanIntervalMinutes) ?? IntervalOptions[1];
        set
        {
            if (value == null || value.Value == _engine.ScanIntervalMinutes) return;
            _ = SaveEngineAsync(_engine with { ScanIntervalMinutes = value.Value });
        }
    }

    // ── History retention (engine) ───────────────────────────────────────────

    public IReadOnlyList<Option<int>> RetentionOptions { get; }

    public Option<int> SelectedRetention
    {
        get => RetentionOptions.FirstOrDefault(o => o.Value == _engine.RetentionDays)
               ?? new Option<int>($"{_engine.RetentionDays} days", _engine.RetentionDays);
        set
        {
            if (value == null || value.Value == _engine.RetentionDays) return;
            _ = ChangeRetentionAsync(value);
        }
    }

    private async Task ChangeRetentionAsync(Option<int> option)
    {
        if (option.Value > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-option.Value);
            var doomed = _shell.AllEvents.Count(e => e.DetectedAt < cutoff);
            if (doomed > 0 && !Dialogs.Confirm("Keep history",
                    $"Delete the {doomed:N0} changes older than {option.Label.ToLowerInvariant()}? This can't be undone.", destructive: true))
            {
                _ = Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedRetention))); // snap the combo back after the binding update finishes
                return;
            }
        }
        await SaveEngineAsync(_engine with { RetentionDays = option.Value });
        OnPropertyChanged(nameof(SelectedRetention));
        await _shell.ReloadHistoryAsync();
    }

    private async Task SaveEngineAsync(EngineSettings next)
    {
        if (!_loaded) return;
        var previous = _engine;
        _engine = next;
        try
        {
            await _shell.Backend.SaveSettingsAsync(next);
            _shell.OnRealtimeChanged();
        }
        catch (Exception ex)
        {
            _engine = previous;
            _shell.ShowMessage($"Couldn't save: {ex.Message}");
        }
        OnPropertiesChanged(nameof(RealtimeEnabled), nameof(SelectedInterval), nameof(SelectedRetention));
    }

    // ── Mode and service ─────────────────────────────────────────────────────

    public string ModeTitle => _shell.Backend.Mode == "Service" ? "Connected to the AppSentry service" : "Monitoring in this app";

    public string ModeBody => _shell.Backend.Mode == "Service"
        ? _shell.CanModify
            ? "The service watches every user on this PC, keeps running when this window is closed, and writes every change to the Windows event log."
            : "You're signed in as a standard user, so you can see everything but only administrators can change settings, exclusions or history."
        : "AppSentry is monitoring as you, only while it's running. Install the service to cover every user and keep monitoring when nobody is signed in.";

    public string ServiceStatusText => ServiceInstaller.GetStatus() switch
    {
        null => "Not installed",
        ServiceControllerStatus.Running => "Running",
        { } s => s.ToString()
    };

    public string ServiceActionText => ServiceInstaller.GetStatus() == null ? "Install service" : "Remove service";

    private async Task ServiceActionAsync()
    {
        var installed = ServiceInstaller.GetStatus() != null;
        var prompt = installed
            ? "Remove the AppSentry service? History is kept, and this app switches back to monitoring on its own after a restart."
            : "Install the AppSentry service?\n\n• Monitors every user on this PC and keeps running when this window is closed\n• Writes every change to the Windows event log\n• Copies AppSentry to Program Files — Windows will ask for administrator approval";
        if (!Dialogs.Confirm(installed ? "Remove service" : "Install service", prompt, destructive: installed)) return;

        var code = await Task.Run(() => ServiceInstaller.RunElevated(installed ? "--uninstall-service" : "--install-service"));
        OnPropertiesChanged(nameof(ServiceStatusText), nameof(ServiceActionText));
        if (code == 1223) { _shell.ShowMessage("Cancelled"); return; }
        if (code != 0) { _shell.ShowMessage($"That didn't work (exit code {code})."); return; }
        if (Dialogs.Confirm("Restart AppSentry", "Done. Restart AppSentry now to switch to the new mode?"))
            ((App)Application.Current).Restart();
    }

    // ── Notifications (per user) ─────────────────────────────────────────────

    public bool NotificationsEnabled
    {
        get => _shell.Settings.NotificationsEnabled;
        set { _shell.Settings.NotificationsEnabled = value; Save(nameof(NotificationsEnabled)); }
    }

    public bool NotifyInstalled { get => Notifies(ChangeType.Installed); set => SetNotify(ChangeType.Installed, value); }
    public bool NotifyUpdated { get => Notifies(ChangeType.Updated); set => SetNotify(ChangeType.Updated, value); }
    public bool NotifyRemoved { get => Notifies(ChangeType.Removed); set => SetNotify(ChangeType.Removed, value); }
    public bool NotifyModified { get => Notifies(ChangeType.Modified); set => SetNotify(ChangeType.Modified, value); }
    public bool NotifyFailed { get => Notifies(ChangeType.Failed); set => SetNotify(ChangeType.Failed, value); }

    public bool AlwaysNotifyAttention
    {
        get => _shell.Settings.AlwaysNotifyAttention;
        set { _shell.Settings.AlwaysNotifyAttention = value; Save(nameof(AlwaysNotifyAttention)); }
    }

    public bool NotificationSound
    {
        get => _shell.Settings.NotificationSound;
        set { _shell.Settings.NotificationSound = value; Save(nameof(NotificationSound)); }
    }

    public bool KeepCriticalOnScreen
    {
        get => _shell.Settings.KeepCriticalOnScreen;
        set { _shell.Settings.KeepCriticalOnScreen = value; Save(nameof(KeepCriticalOnScreen)); }
    }

    public bool IsPaused => _shell.Settings.NotificationsPaused;

    public string PausedText => IsPaused
        ? $"Notifications are paused until {Display.ToLocal(_shell.Settings.NotificationsPausedUntilUtc!.Value):ddd HH:mm}."
        : "";

    public void OnPauseChanged() => OnPropertiesChanged(nameof(IsPaused), nameof(PausedText));

    private bool Notifies(ChangeType type) => _shell.Settings.NotifyTypes.Contains(type);

    private void SetNotify(ChangeType type, bool value)
    {
        if (value) _shell.Settings.NotifyTypes.Add(type);
        else _shell.Settings.NotifyTypes.Remove(type);
        Save("Notify" + type);
    }

    // ── Appearance and startup (per user) ────────────────────────────────────

    public IReadOnlyList<string> ThemeOptions { get; }

    public string SelectedTheme
    {
        get => _shell.Settings.Theme;
        set
        {
            _shell.Settings.Theme = value;
            ((App)Application.Current).ApplyTheme(value);
            Save(nameof(SelectedTheme));
        }
    }

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            try { StartupRegistration.Set(value); }
            catch (Exception ex) { _shell.ShowMessage($"Couldn't change startup: {ex.Message}"); }
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _shell.Settings.CloseToTray;
        set { _shell.Settings.CloseToTray = value; Save(nameof(CloseToTray)); }
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    public string DataDir => _shell.Backend.Mode == "Service" ? ServiceDataDir.Path : LocalBackend.DefaultDataDir;

    public string DataDirNote => _shell.Backend.Mode == "Service" ? "Readable by administrators only." : "";

    public string VersionText => $"AppSentry {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} · {_shell.ModeLabel}";

    private void Export()
    {
        var path = Dialogs.SaveFile("Export history", "CSV files (*.csv)|*.csv", $"AppSentry_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        if (path == null) return;
        try
        {
            EventActions.ExportCsv(_shell.AllEvents, path);
            _shell.ShowMessage($"Exported {_shell.AllEvents.Count:N0} changes to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Export", $"The export didn't finish: {ex.Message}");
        }
    }

    private async Task ClearHistoryAsync()
    {
        if (!Dialogs.Confirm("Clear history", $"Delete all {_shell.AllEvents.Count:N0} recorded changes? This can't be undone.", destructive: true)) return;
        await _shell.Backend.ClearHistoryAsync();
        _shell.ClearLocalHistory();
        _shell.ShowMessage("History cleared");
    }

    private void Save(string property)
    {
        _shell.SaveSettings();
        OnPropertyChanged(property);
    }

    public ICommand ServiceCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand TestNotificationCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand ClearHistoryCommand { get; }
}
