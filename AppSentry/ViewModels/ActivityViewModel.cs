using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

public enum SourceKind { All, Apps, Services, Tasks, Folders }

public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Activity page: summary cards for the chosen period, filter chips, a day-grouped list
/// and a details pane. Filtering runs over the shell's master list; row view models are
/// cached by event id so icons and review state survive refreshes.
/// </summary>
public sealed class ActivityViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;
    private readonly Dictionary<long, EventItemViewModel> _cache = [];
    private Dictionary<string, EventItemViewModel> _groupCache = [];
    private List<EventItemViewModel> _periodRows = [];
    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(200));

    private string _searchText = "";
    // Type chips narrow the list; none selected means every type.
    private bool _filterInstalled, _filterUpdated, _filterRemoved, _filterModified, _filterFailed;
    private bool _needsLookOnly;
    private Option<SourceKind> _source;
    private Option<TimeSpan?> _range;
    private ICollectionView? _events;
    private EventItemViewModel? _selected;
    private EventDetailsViewModel? _details;
    private int _installedCount, _updatedCount, _removedCount, _needsLookCount, _shownCount;
    private bool _suspend;

    public ActivityViewModel(ShellViewModel shell)
    {
        _shell = shell;
        SourceOptions =
        [
            new("All sources", SourceKind.All),
            new("Apps", SourceKind.Apps),
            new("Services and drivers", SourceKind.Services),
            new("Scheduled tasks", SourceKind.Tasks),
            new("Folders", SourceKind.Folders)
        ];
        RangeOptions =
        [
            new("Last 24 hours", TimeSpan.FromDays(1)),
            new("Last 7 days", TimeSpan.FromDays(7)),
            new("Last 30 days", TimeSpan.FromDays(30)),
            new("All time", (TimeSpan?)null)
        ];
        _source = SourceOptions[0];
        _range = RangeOptions[1];

        CardCommand = new RelayCommand(OnCard);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        MarkAllReviewedCommand = new RelayCommand(
            () => _shell.SetReviewed(_shell.AllEvents.Where(e => _shell.IsFlagged(e) && !_shell.IsReviewed(e)).ToList(), true),
            () => NeedsLookCount > 0);
    }

    public IReadOnlyList<Option<SourceKind>> SourceOptions { get; }
    public IReadOnlyList<Option<TimeSpan?>> RangeOptions { get; }

    public ICommand CardCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand MarkAllReviewedCommand { get; }

    /// <summary>Raised when an item should be scrolled into view (the view handles it).</summary>
    public event Action<EventItemViewModel>? ScrollRequested;

    public void OnNavigatedTo() { }

    // ── Filters ───────────────────────────────────────────────────────────────

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) _searchDebounce.Run(Refresh);
        }
    }

    public bool FilterInstalled { get => _filterInstalled; set { if (Set(ref _filterInstalled, value)) Refresh(); } }
    public bool FilterUpdated { get => _filterUpdated; set { if (Set(ref _filterUpdated, value)) Refresh(); } }
    public bool FilterRemoved { get => _filterRemoved; set { if (Set(ref _filterRemoved, value)) Refresh(); } }
    public bool FilterModified { get => _filterModified; set { if (Set(ref _filterModified, value)) Refresh(); } }
    public bool FilterFailed { get => _filterFailed; set { if (Set(ref _filterFailed, value)) Refresh(); } }
    public bool NeedsLookOnly { get => _needsLookOnly; set { if (Set(ref _needsLookOnly, value)) Refresh(); } }
    public Option<SourceKind> SelectedSource { get => _source; set { if (Set(ref _source, value)) Refresh(); } }
    public Option<TimeSpan?> SelectedRange { get => _range; set { if (Set(ref _range, value)) Refresh(); } }

    private bool AnyTypeFilter => _filterInstalled || _filterUpdated || _filterRemoved || _filterModified || _filterFailed;

    public bool HasFiltersActive => _searchText.Length > 0 || _needsLookOnly || _source.Value != SourceKind.All || AnyTypeFilter;

    // ── Cards ─────────────────────────────────────────────────────────────────

    public int InstalledCount { get => _installedCount; private set => Set(ref _installedCount, value); }
    public int UpdatedCount { get => _updatedCount; private set => Set(ref _updatedCount, value); }
    public int RemovedCount { get => _removedCount; private set => Set(ref _removedCount, value); }
    public int NeedsLookCount { get => _needsLookCount; private set => Set(ref _needsLookCount, value); }
    public string RangeLabel => _range.Label.ToLowerInvariant();

    private void OnCard(object? parameter)
    {
        _suspend = true;
        SearchText = "";
        switch (parameter as string)
        {
            case "attention":
                FilterInstalled = FilterUpdated = FilterRemoved = FilterModified = FilterFailed = false;
                NeedsLookOnly = true;
                break;
            case { } type when Enum.TryParse<ChangeType>(type, out var t):
                NeedsLookOnly = false;
                FilterInstalled = t == ChangeType.Installed;
                FilterUpdated = t == ChangeType.Updated;
                FilterRemoved = t == ChangeType.Removed;
                FilterModified = t == ChangeType.Modified;
                FilterFailed = t == ChangeType.Failed;
                break;
        }
        _suspend = false;
        Refresh();
    }

    private void ClearFilters()
    {
        _suspend = true;
        SearchText = "";
        FilterInstalled = FilterUpdated = FilterRemoved = FilterModified = FilterFailed = false;
        NeedsLookOnly = false;
        SelectedSource = SourceOptions[0];
        _suspend = false;
        Refresh();
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public ICollectionView? Events
    {
        get => _events;
        private set => Set(ref _events, value);
    }

    public EventItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Details = value == null ? null : new EventDetailsViewModel(_shell, value);
        }
    }

    public EventDetailsViewModel? Details
    {
        get => _details;
        private set => Set(ref _details, value);
    }

    public int ShownCount { get => _shownCount; private set => Set(ref _shownCount, value); }

    public string ResultText
    {
        get
        {
            if (_shell.AllEvents.Count == 0) return _shell.IsLoadingHistory ? "Loading history…" : "";
            var text = ShownCount == _shell.AllEvents.Count ? $"{ShownCount:N0} changes" : $"Showing {ShownCount:N0} of {_shell.AllEvents.Count:N0} changes";
            // Say what the highlighted chips are doing, so a filter that's on can't go unnoticed.
            var types = new[] { (FilterInstalled, "Installed"), (FilterUpdated, "Updated"), (FilterRemoved, "Removed"), (FilterModified, "Modified"), (FilterFailed, "Failed") }
                .Where(t => t.Item1).Select(t => t.Item2).ToList();
            if (types.Count > 0) text += $" · only {string.Join(", ", types)}";
            return _shell.IsLoadingHistory
                ? $"{text} · loading older changes ({_shell.AllEvents.Count:N0} of {_shell.TotalHistoryCount:N0})"
                : text;
        }
    }

    /// <summary>An older page arrived: update the counter without rebuilding the list every time.</summary>
    public void OnHistoryProgress() => OnPropertyChanged(nameof(ResultText));

    public bool IsEmpty => ShownCount == 0;

    public string EmptyTitle => _shell.AllEvents.Count == 0 ? "Watching for changes" : "Nothing matches these filters";

    public string EmptyBody => _shell.AllEvents.Count == 0
        ? "AppSentry took a baseline of everything installed. Installs, updates, removals and changes to services and scheduled tasks will show up here as they happen."
        : "Try a longer time range or clear the filters.";

    public void Refresh()
    {
        if (_suspend) return;
        var now = DateTime.UtcNow;
        var since = _range.Value is { } span ? now - span : DateTime.MinValue;
        var search = _searchText.Trim().ToLowerInvariant();

        int installed = 0, updated = 0, removed = 0, needsLook = 0, shown = 0;
        var rows = new List<EventItemViewModel>();
        var periodRows = new List<EventItemViewModel>();
        var liveGroups = new Dictionary<string, EventItemViewModel>();

        // One row per change: each user profile's copy of the same Store/app update is folded into one.
        var period = _shell.AllEvents.Where(ev => ev.EffectiveTime >= since && MatchesSource(ev.Source));
        foreach (var group in UserGroups.Group(period, Environment.UserName))
        {
            var item = Row(group, liveGroups);
            periodRows.Add(item);
            var ev = item.Event;

            // Cards count the period and source, ignoring type chips and search.
            switch (ev.ChangeType)
            {
                case ChangeType.Installed: installed++; break;
                case ChangeType.Updated: updated++; break;
                case ChangeType.Removed: removed++; break;
            }
            if (item.NeedsLook) needsLook++;

            if (!MatchesType(ev.ChangeType)) continue;
            if (_needsLookOnly && !item.NeedsLook) continue;
            if (search.Length > 0 && !item.SearchText.Contains(search)) continue;
            rows.Add(item);
            shown += item.Group.Count;
        }
        _groupCache = liveGroups;
        _periodRows = periodRows;

        if (_cache.Count > _shell.AllEvents.Count)
        {
            // History was cleared or reloaded: drop rows for events that no longer exist.
            var allIds = _shell.AllEvents.Select(e => e.Id).ToHashSet();
            foreach (var stale in _cache.Keys.Where(id => !allIds.Contains(id)).ToList()) _cache.Remove(stale);
        }

        // Newest first by the time shown on each row, so day groups never interleave
        // (database ids don't have to follow time: imported v1 history, clock changes).
        rows.Sort((a, b) => b.Event.EffectiveTime.CompareTo(a.Event.EffectiveTime));

        InstalledCount = installed;
        UpdatedCount = updated;
        RemovedCount = removed;
        NeedsLookCount = needsLook;
        ShownCount = shown; // changes, not rows, so "Showing N of M changes" adds up

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EventItemViewModel.DayLabel)));
        Events = view;

        // Keep the selection if it's still visible; otherwise pick the newest row.
        var keep = _selected != null ? rows.FirstOrDefault(r => r.Contains(_selected.Id)) : null;
        Selected = keep ?? rows.FirstOrDefault();

        OnPropertiesChanged(nameof(ResultText), nameof(IsEmpty), nameof(EmptyTitle), nameof(EmptyBody), nameof(HasFiltersActive), nameof(RangeLabel));
    }

    public void OnReviewedChanged()
    {
        foreach (var item in _cache.Values.Concat(_groupCache.Values)) item.IsReviewed = item.Group.All(_shell.IsReviewed);
        NeedsLookCount = _periodRows.Count(i => i.NeedsLook);
        if (_needsLookOnly) Refresh();
    }

    public void Select(long id)
    {
        var ev = _shell.AllEvents.FirstOrDefault(e => e.Id == id);
        if (ev == null) return;
        EventItemViewModel? Find() => Events?.Cast<EventItemViewModel>().FirstOrDefault(r => r.Contains(id));
        if (Find() == null)
        {
            // Make sure it's visible: clear filters and widen the range as needed.
            _suspend = true;
            ClearFilters();
            if (_range.Value is { } span && ev.EffectiveTime < DateTime.UtcNow - span) SelectedRange = RangeOptions[^1];
            _suspend = false;
            Refresh();
        }
        if (Find() is not { } item) return;
        Selected = item;
        ScrollRequested?.Invoke(item);
    }

    /// <summary>A merged row, cached by its events' ids so its icon and state survive refreshes.</summary>
    private EventItemViewModel Row(List<ChangeEvent> group, Dictionary<string, EventItemViewModel> live)
    {
        if (group.Count == 1) return Row(group[0]);
        var key = string.Join(",", group.Select(e => e.Id).Order());
        if (!_groupCache.TryGetValue(key, out var item))
            item = new EventItemViewModel(group[0], group.All(_shell.IsReviewed), group);
        live[key] = item;
        return item;
    }

    private EventItemViewModel Row(ChangeEvent ev)
    {
        if (!_cache.TryGetValue(ev.Id, out var item))
        {
            item = new EventItemViewModel(ev, _shell.IsReviewed(ev));
            if (ev.Id != 0) _cache[ev.Id] = item;
        }
        return item;
    }

    private bool MatchesType(ChangeType type) => !AnyTypeFilter || type switch
    {
        ChangeType.Installed => _filterInstalled,
        ChangeType.Updated => _filterUpdated,
        ChangeType.Removed => _filterRemoved,
        ChangeType.Modified => _filterModified,
        ChangeType.Failed => _filterFailed,
        _ => false
    };

    private bool MatchesSource(DetectionSource source) => _source.Value switch
    {
        SourceKind.Apps => source is DetectionSource.Registry or DetectionSource.Store or DetectionSource.PackageManager or DetectionSource.EventLog,
        SourceKind.Services => source is DetectionSource.Service or DetectionSource.Driver,
        SourceKind.Tasks => source is DetectionSource.ScheduledTask,
        SourceKind.Folders => source is DetectionSource.FileSystem,
        _ => true
    };
}
