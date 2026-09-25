using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AppSentry.Core.Sources;
using AppSentry.Core.Util;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

public enum ScopeKind { All, Machine, User, Store, Scoop }
public enum AppSort { Name, Size, Recent, Publisher }

public sealed class InstalledItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconRequested;

    public InstalledItemViewModel(InstalledApp app, long maxSize)
    {
        App = app;
        SizeBytes = Display.SizeBytes(app);
        SizeFraction = SizeBytes is { } s && maxSize > 0 ? (double)s / maxSize : 0;
        InstalledOn = ParseInstallDate(app);
        SearchText = $"{app.Name} {app.Publisher} {app.Version} {app.InstallLocation} {app.InstalledFor} {app.PackageManager}".ToLowerInvariant();
    }

    public InstalledApp App { get; }
    public string Title => App.Name;
    public string Subtitle => string.Join(" · ", new[] { App.Publisher, App.Version }.Where(s => s.Length > 0));
    public string InstalledFor => App.InstalledFor;
    public long? SizeBytes { get; }
    public string SizeText => Display.Size(SizeBytes);
    public double SizeFraction { get; }
    public DateTime? InstalledOn { get; }
    public string InstalledOnText => InstalledOn?.ToString("yyyy-MM-dd") ?? "";
    public string Glyph => Scopes.SourceOf(App.Scope) == DetectionSource.Store ? Display.Glyphs.Store
        : Scopes.SourceOf(App.Scope) == DetectionSource.PackageManager ? Display.Glyphs.Package : Display.Glyphs.App;
    public string SearchText { get; }

    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                if (AppIconCache.TryGetCached(App, out var cached)) _icon = cached;
                else _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    public bool HasIcon => Icon != null;

    private async Task LoadIconAsync()
    {
        var icon = await AppIconCache.LoadAsync(App);
        if (icon == null) return;
        _icon = icon;
        ShellViewModel.OnUi(() => OnPropertiesChanged(nameof(Icon), nameof(HasIcon)));
    }

    private static DateTime? ParseInstallDate(InstalledApp app)
    {
        if (DateTime.TryParseExact(app.InstallDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return app.KeyLastWriteUtc is { } w ? Display.ToLocal(w).Date : null;
    }
}

/// <summary>The Installed apps page: current inventory with icons, size bars, filters and per-app history.</summary>
public sealed class InstalledViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;
    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(200));
    private List<InstalledItemViewModel>? _all;
    private ICollectionView? _apps;
    private string _searchText = "";
    private Option<ScopeKind> _scope;
    private Option<AppSort> _sort;
    private bool _recentOnly;
    private bool _isLoading;
    private InstalledItemViewModel? _selected;
    private InstalledDetailsViewModel? _details;
    private string _countText = "";
    private bool _stale;

    public InstalledViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ScopeOptions =
        [
            new("Everything", ScopeKind.All),
            new("Machine-wide", ScopeKind.Machine),
            new("Per-user", ScopeKind.User),
            new("Microsoft Store", ScopeKind.Store),
            new("Scoop", ScopeKind.Scoop)
        ];
        SortOptions =
        [
            new("Name", AppSort.Name),
            new("Size", AppSort.Size),
            new("Recently installed", AppSort.Recent),
            new("Publisher", AppSort.Publisher)
        ];
        _scope = ScopeOptions[0];
        _sort = SortOptions[0];
        RefreshCommand = new AsyncCommand(LoadAsync);
    }

    public IReadOnlyList<Option<ScopeKind>> ScopeOptions { get; }
    public IReadOnlyList<Option<AppSort>> SortOptions { get; }
    public ICommand RefreshCommand { get; }

    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) _searchDebounce.Run(ApplyFilter); } }
    public Option<ScopeKind> SelectedScope { get => _scope; set { if (Set(ref _scope, value)) ApplyFilter(); } }
    public Option<AppSort> SelectedSort { get => _sort; set { if (Set(ref _sort, value)) ApplyFilter(); } }
    public bool RecentOnly { get => _recentOnly; set { if (Set(ref _recentOnly, value)) ApplyFilter(); } }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public ICollectionView? Apps { get => _apps; private set => Set(ref _apps, value); }

    public InstalledItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
                Details = value == null ? null : new InstalledDetailsViewModel(_shell, value);
        }
    }

    public InstalledDetailsViewModel? Details { get => _details; private set => Set(ref _details, value); }

    public void OnNavigatedTo()
    {
        if (_all == null || _stale) _ = LoadAsync();
    }

    /// <summary>A scan finished: reload if the page is showing, otherwise next time it's opened.</summary>
    public void OnScanCompleted()
    {
        if (_all == null) return;
        if (_shell.CurrentPage == this) _ = LoadAsync();
        else _stale = true;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var apps = await _shell.Backend.GetInventoryAsync();
            var maxSize = apps.Select(a => Display.SizeBytes(a) ?? 0).DefaultIfEmpty(0).Max();
            _all = apps.Select(a => new InstalledItemViewModel(a, maxSize)).ToList();
            _stale = false;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        if (_all == null) return;
        var search = _searchText.Trim().ToLowerInvariant();
        var recentCutoff = DateTime.Today.AddDays(-7);

        IEnumerable<InstalledItemViewModel> query = _all.Where(i =>
            MatchesScope(i.App.Scope) &&
            (!_recentOnly || i.InstalledOn >= recentCutoff) &&
            (search.Length == 0 || i.SearchText.Contains(search)));

        query = _sort.Value switch
        {
            AppSort.Size => query.OrderByDescending(i => i.SizeBytes ?? 0),
            AppSort.Recent => query.OrderByDescending(i => i.InstalledOn ?? DateTime.MinValue),
            AppSort.Publisher => query.OrderBy(i => i.App.Publisher, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
        };

        var list = query.ToList();
        Apps = new ListCollectionView(list);
        CountText = list.Count == _all.Count ? $"{_all.Count:N0} apps" : $"{list.Count:N0} of {_all.Count:N0} apps";
        Selected = _selected != null ? list.FirstOrDefault(i => i.App.KeyPath == _selected.App.KeyPath) ?? list.FirstOrDefault() : list.FirstOrDefault();
    }

    private bool MatchesScope(string scope) => _scope.Value switch
    {
        ScopeKind.Machine => scope is Scopes.Machine64 or Scopes.Machine32,
        ScopeKind.User => scope.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase),
        ScopeKind.Store => scope.StartsWith(@"STORE\", StringComparison.OrdinalIgnoreCase),
        ScopeKind.Scoop => scope.StartsWith(@"SCOOP\", StringComparison.OrdinalIgnoreCase),
        _ => true
    };
}

public sealed class InstalledDetailsViewModel
{
    public InstalledDetailsViewModel(ShellViewModel shell, InstalledItemViewModel item)
    {
        Item = item;
        var app = item.App;
        Facts = new[]
        {
            new Fact("Version", app.Version),
            new Fact("Publisher", app.Publisher),
            new Fact("Installed for", app.InstalledFor),
            new Fact("Installed by", app.InstalledBy),
            new Fact("Install date", item.InstalledOnText),
            new Fact("Size", item.SizeText),
            new Fact("Location", app.InstallLocation),
            new Fact("Install type", app.InstallType),
            new Fact("Package manager", app.PackageManager),
            new Fact("Install source", app.InstallSource),
            new Fact(app.PackageFullName.Length > 0 ? "Package" : "Registry key", app.PackageFullName.Length > 0 ? app.PackageFullName : app.KeyPath)
        }.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();

        var name = NameNormalizer.Normalize(app.Name);
        History = shell.AllEvents
            .Where(e => e.App.KeyPath.Equals(app.KeyPath, StringComparison.OrdinalIgnoreCase) ||
                        (e.PreviousApp?.KeyPath.Equals(app.KeyPath, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (name.Length >= 3 && NameNormalizer.Normalize(e.App.Name) == name))
            .Take(50)
            .Select(e => new EventItemViewModel(e, shell.IsReviewed(e)))
            .ToList();

        CanUninstall = EventActions.CanUninstall(app);
        CanOpenFolder = app.InstallLocation.Length > 0 && Directory.Exists(app.InstallLocation);
        CanExclude = shell.CanModify;
        UninstallCommand = new AsyncCommand(async () =>
        {
            var message = await EventActions.UninstallAsync(app);
            if (message != null) shell.ShowMessage(message);
        });
        OpenFolderCommand = new RelayCommand(() => Dialogs.OpenFolder(app.InstallLocation));
        ExcludeCommand = new AsyncCommand(() => shell.ExcludeAsync(new ChangeEvent { App = app }));
        CopyCommand = new RelayCommand(() =>
        {
            Dialogs.CopyText(string.Join(Environment.NewLine, Facts.Prepend(new Fact("App", app.Name)).Select(f => $"{f.Label + ":",-17} {f.Value}")));
            shell.ShowMessage("Details copied");
        });
        ShowEventCommand = new RelayCommand(p => { if (p is EventItemViewModel e) shell.ShowEvent(e.Id); });
    }

    public InstalledItemViewModel Item { get; }
    public IReadOnlyList<Fact> Facts { get; }
    public bool CanUninstall { get; }
    public bool CanOpenFolder { get; }
    public bool CanExclude { get; }
    public IReadOnlyList<EventItemViewModel> History { get; }
    public bool HasHistory => History.Count > 0;
    public ICommand UninstallCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ExcludeCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ShowEventCommand { get; }
}
