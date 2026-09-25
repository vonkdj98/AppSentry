using System.Collections.ObjectModel;
using System.Windows.Input;
using AppSentry.Core.Detection;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

public sealed class ExclusionRowViewModel(ExclusionsViewModel owner, ExclusionEntry entry) : ObservableObject
{
    private ExclusionEntry _entry = entry;
    private string _matchText = "";

    public ExclusionEntry Entry => _entry;
    public string Pattern => _entry.AppName;

    public bool DontNotify
    {
        get => _entry.ExcludeNotifications || _entry.ExcludeLogging;
        set
        {
            if (value == DontNotify) return;
            _entry = _entry with { ExcludeNotifications = value, ExcludeLogging = value && _entry.ExcludeLogging };
            OnPropertiesChanged(nameof(DontNotify), nameof(DontLog), nameof(Effect));
            owner.QueueSave();
        }
    }

    public bool DontLog
    {
        get => _entry.ExcludeLogging;
        set
        {
            if (value == DontLog) return;
            _entry = _entry with { ExcludeLogging = value, ExcludeNotifications = value || _entry.ExcludeNotifications };
            OnPropertiesChanged(nameof(DontNotify), nameof(DontLog), nameof(Effect));
            owner.QueueSave();
        }
    }

    public string Effect => _entry.ExcludeLogging ? "Not recorded at all" : "Recorded, no notification";

    public string MatchText
    {
        get => _matchText;
        set => Set(ref _matchText, value);
    }

    public void Replace(ExclusionEntry entry)
    {
        _entry = entry;
        OnPropertiesChanged(nameof(Entry), nameof(Pattern), nameof(DontNotify), nameof(DontLog), nameof(Effect));
    }
}

/// <summary>Exclusions, edited in place and saved back to the engine as you go.</summary>
public sealed class ExclusionsViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;
    private readonly Debouncer _saveDebounce = new(TimeSpan.FromMilliseconds(600));

    public ExclusionsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        AddCommand = new AsyncCommand(async () =>
        {
            var entry = Views.ExclusionDialog.Show(new ExclusionEntry("", true, false), _shell.AllEvents, isNew: true);
            if (entry != null) await AddOrUpdateAsync(entry, null);
        }, () => _shell.CanModify);
        EditCommand = new AsyncCommand(async p =>
        {
            if (p is not ExclusionRowViewModel row) return;
            var entry = Views.ExclusionDialog.Show(row.Entry, _shell.AllEvents, isNew: false);
            if (entry != null) await AddOrUpdateAsync(entry, row);
        }, _ => _shell.CanModify);
        RemoveCommand = new AsyncCommand(async p =>
        {
            if (p is not ExclusionRowViewModel row) return;
            if (!Dialogs.Confirm("Remove exclusion", $"Stop excluding \"{row.Pattern}\"? It will be recorded and notify normally again.")) return;
            Rows.Remove(row);
            await SaveAsync();
            OnPropertyChanged(nameof(IsEmpty));
        }, _ => _shell.CanModify);
    }

    public ObservableCollection<ExclusionRowViewModel> Rows { get; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public bool CanModify => _shell.CanModify;

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }

    public void OnNavigatedTo() => RefreshMatchCounts();

    public async Task LoadAsync()
    {
        var entries = await _shell.Backend.GetExclusionsAsync();
        Rows.Clear();
        foreach (var e in entries) Rows.Add(new ExclusionRowViewModel(this, e));
        OnPropertiesChanged(nameof(IsEmpty), nameof(CanModify));
        RefreshMatchCounts();
    }

    public async Task AddOrUpdateAsync(ExclusionEntry entry, ExclusionRowViewModel? existing)
    {
        var same = Rows.FirstOrDefault(r => r != existing && r.Entry.AppName.Equals(entry.AppName, StringComparison.OrdinalIgnoreCase));
        if (same != null) Rows.Remove(same);
        if (existing != null) existing.Replace(entry);
        else Rows.Add(new ExclusionRowViewModel(this, entry));
        await SaveAsync();
        RefreshMatchCounts();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void QueueSave() => _saveDebounce.Run(async () =>
    {
        try { await SaveAsync(); }
        catch (Exception ex) { _shell.ShowMessage($"Couldn't save exclusions: {ex.Message}"); }
    });

    private async Task SaveAsync()
    {
        await _shell.Backend.SaveExclusionsAsync(Rows.Select(r => r.Entry).ToList());
    }

    /// <summary>"Matches 12 past events" for each row, so a pattern's reach is visible before it bites.</summary>
    public void RefreshMatchCounts()
    {
        if (Rows.Count == 0) return;
        var byName = _shell.AllEvents.GroupBy(e => e.App.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count())).ToList();
        foreach (var row in Rows)
        {
            var matcher = new ExclusionMatcher([row.Entry]);
            var count = byName.Where(n => matcher.Match(n.Name) != null).Sum(n => n.Count);
            row.MatchText = count == 0 ? "No past events match" : $"Matches {count:N0} past event{(count == 1 ? "" : "s")}";
        }
    }
}
