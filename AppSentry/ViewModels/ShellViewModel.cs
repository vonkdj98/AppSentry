using System.Windows;
using System.Windows.Input;
using AppSentry.Core.Backend;
using AppSentry.Core.Detection;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

/// <summary>Pages get a chance to load lazily the first time they're shown.</summary>
public interface IPage
{
    void OnNavigatedTo();
}

public sealed class NavItem(string title, string glyph, IPage page) : ObservableObject
{
    private bool _isSelected;

    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public IPage Page { get; } = page;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed record SourceHealthRow(string Name, string State, Tone Tone);

/// <summary>
/// The window's root: navigation, engine status, the master event list every page reads from,
/// "needs a look" bookkeeping and the transient message bar.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly HashSet<long> _knownIds = [];
    private NavItem _selectedNav;
    private EngineStatus _status;
    private int _needsLookCount;
    private string? _message;
    private CancellationTokenSource? _messageCts;
    private DateTime? _lastScanSeen;

    public ShellViewModel(IMonitorBackend backend, UiSettings settings)
    {
        Backend = backend;
        Settings = settings;
        _status = backend.Status;

        Activity = new ActivityViewModel(this);
        Installed = new InstalledViewModel(this);
        Persistence = new PersistenceViewModel(this);
        Exclusions = new ExclusionsViewModel(this);
        SettingsPage = new SettingsViewModel(this);

        var nav = new List<NavItem>
        {
            new("Activity", Display.Glyphs.Activity, Activity),
            new("Installed apps", Display.Glyphs.AllApps, Installed),
            new("Services and tasks", Display.Glyphs.Service, Persistence),
            new("Exclusions", Display.Glyphs.Filter, Exclusions)
        };
        AddEditionPages(nav);
        NavItems = nav;
        SettingsNav = new NavItem("Settings", Display.Glyphs.Settings, SettingsPage);
        _selectedNav = NavItems[0];
        _selectedNav.IsSelected = true;

        NavigateCommand = new RelayCommand(p => { if (p is NavItem item) SelectedNav = item; });
        ScanCommand = new AsyncCommand(() => Backend.RequestScanAsync(), onError: ex => ShowMessage($"Couldn't start a scan: {ex.Message}"));
        DismissMessageCommand = new RelayCommand(() => Message = null);

        backend.EventsDetected += (_, events) => OnUi(() => OnEventsDetected(events));
        backend.StatusChanged += (_, status) => OnUi(() => Status = status);
    }

    public IMonitorBackend Backend { get; }
    public UiSettings Settings { get; }

    public ActivityViewModel Activity { get; }
    public InstalledViewModel Installed { get; }
    public PersistenceViewModel Persistence { get; }
    public ExclusionsViewModel Exclusions { get; }
    public SettingsViewModel SettingsPage { get; }

    public IReadOnlyList<NavItem> NavItems { get; }
    public NavItem SettingsNav { get; }

    public ICommand NavigateCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand DismissMessageCommand { get; }

    /// <summary>Every recorded change, newest first.</summary>
    public List<ChangeEvent> AllEvents { get; private set; } = [];

    /// <summary>New events for notifications and the tray (UI thread).</summary>
    public event Action<IReadOnlyList<ChangeEvent>>? NewEvents;

    /// <summary>"Needs a look" count or pause state changed.</summary>
    public event Action? AttentionChanged;

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value == _selectedNav) return;
            _selectedNav.IsSelected = false;
            _selectedNav = value;
            value.IsSelected = true;
            OnPropertiesChanged(nameof(SelectedNav), nameof(CurrentPage));
            value.Page.OnNavigatedTo();
        }
    }

    public IPage CurrentPage => _selectedNav.Page;

    public bool CanModify => Backend.CanModify;

    public bool IsReadOnly => !Backend.CanModify;

    public string ModeLabel => Backend.Mode == "Service" ? "Service mode" : "Local mode";

    // ── Status ────────────────────────────────────────────────────────────────

    public EngineStatus Status
    {
        get => _status;
        private set
        {
            _status = value;
            OnPropertiesChanged(nameof(Status), nameof(StatusText), nameof(StatusDetail), nameof(StatusTone), nameof(IsScanning),
                nameof(LastScanText), nameof(SourceRows), nameof(StatusProblem));
            if (value.LastScanUtc != _lastScanSeen && !value.IsScanning)
            {
                _lastScanSeen = value.LastScanUtc;
                Installed.OnScanCompleted();
                Persistence.OnScanCompleted();
            }
            AttentionChanged?.Invoke();
        }
    }

    public bool IsScanning => _status.IsScanning;

    public string StatusText =>
        _status.IsScanning ? "Scanning…" :
        _status.LastError != null ? "Needs attention" :
        _status.LastScanUtc == null ? "Starting…" : "Monitoring";

    public string StatusDetail
    {
        get
        {
            var realtime = SettingsPage.RealtimeEnabled ? "real-time" : "interval only";
            return $"{ModeLabel} · {realtime}";
        }
    }

    public Tone StatusTone =>
        _status.LastError != null ? Tone.Critical :
        _status.IsScanning ? Tone.Accent :
        _status.Sources.Values.Any(IsDegraded) ? Tone.Caution :
        _status.LastScanUtc == null ? Tone.Neutral : Tone.Success;

    public string? StatusProblem => _status.LastError ?? _status.Notice;

    public string LastScanText => _status.LastScanUtc is { } last
        ? $"Last scan {Display.ShortTime(last)}{(_status.LastScanSeconds is { } s ? $" · {s:0.0}s" : "")} · {_status.TrackedApps} apps"
        : "Waiting for the first scan";

    public IReadOnlyList<SourceHealthRow> SourceRows => _status.Sources
        .OrderBy(kv => kv.Key)
        .Select(kv => new SourceHealthRow(kv.Key, kv.Value, kv.Value.StartsWith("failed") ? Tone.Critical : IsDegraded(kv.Value) ? Tone.Caution : Tone.Success))
        .ToList();

    private static bool IsDegraded(string state) => !state.StartsWith("ok", StringComparison.OrdinalIgnoreCase);

    // ── Needs a look ─────────────────────────────────────────────────────────

    public int NeedsLookCount
    {
        get => _needsLookCount;
        private set => Set(ref _needsLookCount, value);
    }

    public bool IsFlagged(ChangeEvent ev) => AttentionClassifier.Classify(ev).Level >= AttentionLevel.Warning;

    public bool IsReviewed(ChangeEvent ev) => Settings.ReviewedEventIds.Contains(ev.Id);

    public void SetReviewed(IEnumerable<ChangeEvent> events, bool reviewed)
    {
        foreach (var ev in events)
        {
            if (reviewed) Settings.ReviewedEventIds.Add(ev.Id);
            else Settings.ReviewedEventIds.Remove(ev.Id);
        }
        SaveSettings();
        RecountAttention();
        Activity.OnReviewedChanged();
    }

    private void RecountAttention()
    {
        NeedsLookCount = AllEvents.Count(e => IsFlagged(e) && !IsReviewed(e));
        AttentionChanged?.Invoke();
    }

    // ── Lifecycle / data ─────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await Backend.StartAsync();
        Status = Backend.Status;
        await ReloadHistoryAsync();
        await Exclusions.LoadAsync();
        await SettingsPage.LoadAsync();
        OnPropertiesChanged(nameof(CanModify), nameof(IsReadOnly), nameof(ModeLabel), nameof(StatusDetail));
    }

    private const int FirstPageSize = 1000;
    private const int PageSize = 5000;
    private bool _isLoadingHistory;
    private int _totalHistoryCount;

    /// <summary>True while older pages of history are still arriving.</summary>
    public bool IsLoadingHistory
    {
        get => _isLoadingHistory;
        private set => Set(ref _isLoadingHistory, value);
    }

    /// <summary>How many changes the engine holds in total (from the last page).</summary>
    public int TotalHistoryCount
    {
        get => _totalHistoryCount;
        private set => Set(ref _totalHistoryCount, value);
    }

    /// <summary>
    /// Loads history in pages: the newest page is shown immediately, older pages stream in behind
    /// it. Pages are "slim" (no raw registry values), so even a large history is a few MB.
    /// </summary>
    public async Task ReloadHistoryAsync()
    {
        IsLoadingHistory = true;
        try
        {
            var page = await Backend.GetHistoryPageAsync(new HistoryQuery { Limit = FirstPageSize });
            TotalHistoryCount = page.TotalCount;
            var ids = page.Events.Select(e => e.Id).ToHashSet();
            // Keep anything a scan delivered while the history was loading.
            AllEvents = AllEvents.Where(e => !ids.Contains(e.Id)).Concat(page.Events).ToList();
            _knownIds.Clear();
            _knownIds.UnionWith(AllEvents.Select(e => e.Id));
            Activity.Refresh();
            RecountAttention();

            while (page.HasMore && page.Events.Count > 0)
            {
                page = await Backend.GetHistoryPageAsync(new HistoryQuery { BeforeId = page.Events[^1].Id, Limit = PageSize });
                AllEvents.AddRange(page.Events.Where(e => _knownIds.Add(e.Id)));
                Activity.OnHistoryProgress();
            }

            // Only once everything is here: forget "reviewed" marks for events that no longer exist.
            if (Settings.ReviewedEventIds.RemoveWhere(id => !_knownIds.Contains(id)) > 0) SaveSettings();
        }
        finally
        {
            IsLoadingHistory = false;
            Activity.Refresh();
            Exclusions.RefreshMatchCounts();
            RecountAttention();
        }
    }

    private void OnEventsDetected(IReadOnlyList<ChangeEvent> events)
    {
        var added = events.Where(e => e.Id == 0 || _knownIds.Add(e.Id)).OrderByDescending(e => e.DetectedAt).ToList();
        if (added.Count == 0) return;
        AllEvents.InsertRange(0, added);
        Activity.Refresh();
        Exclusions.RefreshMatchCounts();
        RecountAttention();
        NewEvents?.Invoke(added);
    }

    public void ClearLocalHistory()
    {
        AllEvents = [];
        _knownIds.Clear();
        Settings.ReviewedEventIds.Clear();
        SaveSettings();
        Activity.Refresh();
        RecountAttention();
    }

    public void SaveSettings() => UiSettingsStore.Save(Settings);

    public void PauseNotifications(TimeSpan? duration)
    {
        Settings.NotificationsPausedUntilUtc = duration is { } d ? DateTime.UtcNow + d : null;
        SaveSettings();
        SettingsPage.OnPauseChanged();
        AttentionChanged?.Invoke();
        ShowMessage(duration == null ? "Notifications resumed" : $"Notifications paused until {Display.ToLocal(Settings.NotificationsPausedUntilUtc!.Value):HH:mm}");
    }

    public void OnRealtimeChanged() => OnPropertyChanged(nameof(StatusDetail));

    // ── Navigation helpers ───────────────────────────────────────────────────

    public void ShowEvent(long id)
    {
        SelectedNav = NavItems[0];
        Activity.Select(id);
    }

    public async Task ExcludeAsync(ChangeEvent ev)
    {
        var result = Views.ExclusionDialog.Show(new ExclusionEntry(ev.App.Name, true, false), AllEvents, isNew: true);
        if (result == null) return;
        await Exclusions.AddOrUpdateAsync(result, null);
        ShowMessage($"Exclusion saved for {Display.CleanName(result.AppName)}");
    }

    // ── Message bar ──────────────────────────────────────────────────────────

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public async void ShowMessage(string text)
    {
        _messageCts?.Cancel();
        var cts = _messageCts = new CancellationTokenSource();
        Message = text;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
            Message = null;
        }
        catch (TaskCanceledException) { }
    }

    public static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action(); // no UI thread (tests) or already on it
        else dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// Lets an edition add pages to the navigation (and register their view templates).
    /// The open-source build has none. See AppSentry.Core/Editions/EditionTypes.cs.
    /// </summary>
    partial void AddEditionPages(List<NavItem> nav);
}
