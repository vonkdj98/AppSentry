using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AppSentry.Core.Sources;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

public enum PersistenceKind { Services, Drivers, Tasks }

public sealed class PersistenceRowViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconRequested;
    private readonly InstalledApp _iconSource;

    public PersistenceRowViewModel(string title, string subtitle, string glyph, bool isMicrosoft, string keyPath,
        IReadOnlyList<Fact> facts, Dictionary<string, string> iconValues)
    {
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        IsMicrosoft = isMicrosoft;
        KeyPath = keyPath;
        Facts = facts;
        SearchText = $"{title} {subtitle} {string.Join(" ", facts.Select(f => f.Value))}".ToLowerInvariant();
        _iconSource = new InstalledApp { KeyPath = keyPath, RawValues = iconValues };
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public bool IsMicrosoft { get; }
    public string KeyPath { get; }
    public IReadOnlyList<Fact> Facts { get; }
    public string SearchText { get; }
    public ToneBadge? MicrosoftBadge => IsMicrosoft ? new ToneBadge("Microsoft", Tone.Neutral) : null;

    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                if (AppIconCache.TryGetCached(_iconSource, out var cached)) _icon = cached;
                else _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    public bool HasIcon => Icon != null;

    private async Task LoadIconAsync()
    {
        var icon = await AppIconCache.LoadAsync(_iconSource);
        if (icon == null) return;
        _icon = icon;
        ShellViewModel.OnUi(() => OnPropertiesChanged(nameof(Icon), nameof(HasIcon)));
    }
}

/// <summary>
/// Services, drivers and scheduled tasks the engine is tracking — the places software goes to
/// keep itself running. Microsoft's own components are hidden by default.
/// </summary>
public sealed class PersistenceViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;
    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(200));
    private PersistenceInventory? _inventory;
    private PersistenceKind _kind = PersistenceKind.Services;
    private bool _hideMicrosoft = true;
    private string _searchText = "";
    private ICollectionView? _rows;
    private PersistenceRowViewModel? _selected;
    private string _countText = "";
    private string? _error;
    private bool _isLoading;
    private bool _stale;

    public PersistenceViewModel(ShellViewModel shell)
    {
        _shell = shell;
        RefreshCommand = new AsyncCommand(LoadAsync);
        ShowEventCommand = new RelayCommand(p => { if (p is EventItemViewModel e) shell.ShowEvent(e.Id); });
    }

    public ICommand RefreshCommand { get; }
    public ICommand ShowEventCommand { get; }

    public bool IsServices { get => _kind == PersistenceKind.Services; set { if (value) SetKind(PersistenceKind.Services); } }
    public bool IsDrivers { get => _kind == PersistenceKind.Drivers; set { if (value) SetKind(PersistenceKind.Drivers); } }
    public bool IsTasks { get => _kind == PersistenceKind.Tasks; set { if (value) SetKind(PersistenceKind.Tasks); } }

    public bool HideMicrosoft { get => _hideMicrosoft; set { if (Set(ref _hideMicrosoft, value)) Apply(); } }
    public bool CanHideMicrosoft => _kind != PersistenceKind.Tasks;
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) _searchDebounce.Run(Apply); } }
    public ICollectionView? Rows { get => _rows; private set => Set(ref _rows, value); }
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public PersistenceRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) OnPropertiesChanged(nameof(SelectedHistory), nameof(HasSelectedHistory));
        }
    }

    public IReadOnlyList<EventItemViewModel> SelectedHistory => _selected == null ? [] : _shell.AllEvents
        .Where(e => e.App.KeyPath.Equals(_selected.KeyPath, StringComparison.OrdinalIgnoreCase))
        .Take(30)
        .Select(e => new EventItemViewModel(e, _shell.IsReviewed(e)))
        .ToList();

    public bool HasSelectedHistory => SelectedHistory.Count > 0;

    public string Description => _kind switch
    {
        PersistenceKind.Services => "Services start with Windows or on demand and usually run with high privileges.",
        PersistenceKind.Drivers => "Drivers run inside the Windows kernel. New ones deserve a look.",
        _ => "Scheduled tasks run programs on a timer or at sign-in. Windows' own tasks are hidden."
    };

    public void OnNavigatedTo()
    {
        if (_inventory == null || _stale) _ = LoadAsync();
    }

    public void OnScanCompleted()
    {
        if (_inventory == null) return;
        if (_shell.CurrentPage == this) _ = LoadAsync();
        else _stale = true;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _inventory = await _shell.Backend.GetPersistenceAsync();
            Error = null;
            _stale = false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unknown operation"))
        {
            Error = "The installed AppSentry service is older than this app. Reinstall it from Settings to see this list.";
            _inventory = new PersistenceInventory();
        }
        finally
        {
            IsLoading = false;
        }
        Apply();
    }

    private void SetKind(PersistenceKind kind)
    {
        if (_kind == kind) return;
        _kind = kind;
        OnPropertiesChanged(nameof(IsServices), nameof(IsDrivers), nameof(IsTasks), nameof(CanHideMicrosoft), nameof(Description));
        Apply();
    }

    private void Apply()
    {
        if (_inventory == null) return;
        var search = _searchText.Trim().ToLowerInvariant();
        IEnumerable<PersistenceRowViewModel> rows = _kind switch
        {
            PersistenceKind.Tasks => _inventory.Tasks.Select(TaskRow),
            PersistenceKind.Drivers => _inventory.Services.Where(s => s.IsDriver).Select(ServiceRow),
            _ => _inventory.Services.Where(s => !s.IsDriver).Select(ServiceRow)
        };
        var all = rows.ToList();
        var shown = all.Where(r => (!_hideMicrosoft || !r.IsMicrosoft || _kind == PersistenceKind.Tasks) &&
                                   (search.Length == 0 || r.SearchText.Contains(search))).ToList();

        Rows = new ListCollectionView(shown);
        var hidden = _hideMicrosoft && _kind != PersistenceKind.Tasks ? all.Count(r => r.IsMicrosoft) : 0;
        CountText = $"{shown.Count:N0} shown{(hidden > 0 ? $" · {hidden:N0} Microsoft components hidden" : "")}";
        Selected = _selected != null ? shown.FirstOrDefault(r => r.KeyPath == _selected.KeyPath) ?? shown.FirstOrDefault() : shown.FirstOrDefault();
    }

    private static PersistenceRowViewModel ServiceRow(ServiceRecord s)
    {
        var start = s.Start switch { 0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown" };
        var account = s.Account.Length > 0 ? s.Account : s.IsDriver ? "Kernel" : "LocalSystem";
        var binary = s.ServiceDll.Length > 0 ? s.ServiceDll : s.ImagePath;
        return new PersistenceRowViewModel(
            s.DisplayName.Length > 0 ? s.DisplayName : s.Name,
            $"{s.Name} · {start}{(s.IsDriver ? "" : $" · {account}")}",
            s.IsDriver ? Display.Glyphs.Driver : Display.Glyphs.Service,
            s.IsMicrosoft,
            $@"{(s.IsDriver ? "DRIVER" : "SERVICE")}\{s.Name}",
            new[]
            {
                new Fact("Name", s.Name),
                new Fact("Start", start),
                new Fact("Runs as", s.IsDriver ? "" : account),
                new Fact("Binary", binary),
                new Fact("Service DLL", s.ServiceDll.Length > 0 ? s.ImagePath : ""),
                new Fact("Publisher", s.IsMicrosoft ? "Microsoft" : "Third party")
            }.Where(f => f.Value.Length > 0).ToList(),
            new Dictionary<string, string> { ["ImagePath"] = s.ImagePath, ["ServiceDll"] = s.ServiceDll });
    }

    private static PersistenceRowViewModel TaskRow(TaskRecord t)
    {
        var runAs = $"{(t.RunAs.Length > 0 ? t.RunAs : "the creator")}{(t.Highest ? " (highest privileges)" : "")}";
        return new PersistenceRowViewModel(
            t.Path.TrimStart('\\'),
            $"Runs as {runAs}{(t.Enabled ? "" : " · disabled")}",
            Display.Glyphs.Task,
            false,
            $@"SCHEDULEDTASK\{t.Path}",
            new[]
            {
                new Fact("Path", t.Path),
                new Fact("Runs", t.Actions),
                new Fact("Runs as", runAs),
                new Fact("Author", t.Author),
                new Fact("Enabled", t.Enabled ? "Yes" : "No")
            }.Where(f => f.Value.Length > 0).ToList(),
            new Dictionary<string, string> { ["Actions"] = t.Actions });
    }
}
