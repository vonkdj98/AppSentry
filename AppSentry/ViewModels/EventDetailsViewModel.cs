using System.IO;
using System.Windows.Input;
using AppSentry.Core.Detection;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

public sealed record Fact(string Label, string Value);

/// <summary>The details pane for one change: what happened, why it's flagged, the diff, the facts, actions.</summary>
public sealed class EventDetailsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _showUnchanged;

    public EventDetailsViewModel(ShellViewModel shell, EventItemViewModel item)
    {
        _shell = shell;
        Item = item;
        var ev = item.Event;
        var app = ev.App;

        var isPersistence = ev.Source is DetectionSource.Service or DetectionSource.Driver or DetectionSource.ScheduledTask;
        Sentence = SentenceFor(ev);

        // Publisher · install technology — or the source when there's no technology to name.
        var kind = app.InstallType.Length > 0 && !app.InstallType.StartsWith("Portable", StringComparison.OrdinalIgnoreCase)
            ? app.InstallType
            : Display.SourceLabel(ev.Source);
        SubtitleLine = string.Join(" · ", new[] { app.Publisher, kind }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
        AllDiffRows = EventActions.Diff(ev);

        var raw = app.RawValues ?? [];
        var facts = new List<Fact>
        {
            new("Changed by", ev.ChangedBy),
            new("Installed for", isPersistence ? "" : app.InstalledFor),
            new("Happened", ev.OccurredAt is { } at ? Display.LocalTime(at) : ""),
            new("Detected", Display.LocalTime(ev.DetectedAt)),
            new("Version", app.Version),
            new("Previous version", ev.PreviousVersion ?? ""),
            new("Size", Display.Size(Display.SizeBytes(ev)))
        };
        if (ev.Source is DetectionSource.Service or DetectionSource.Driver)
        {
            facts.Add(new("Service name", raw.GetValueOrDefault("ServiceName") ?? ""));
            facts.Add(new("Binary", raw.GetValueOrDefault("ImagePath") ?? ""));
            facts.Add(new("Service DLL", raw.GetValueOrDefault("ServiceDll") ?? ""));
            facts.Add(new("Start", raw.GetValueOrDefault("Start") ?? ""));
            facts.Add(new("Runs as", raw.GetValueOrDefault("ObjectName") ?? ""));
        }
        else if (ev.Source == DetectionSource.ScheduledTask)
        {
            facts.Add(new("Runs", raw.GetValueOrDefault("Actions") ?? ""));
            facts.Add(new("Runs as", raw.GetValueOrDefault("RunAs") ?? ""));
            facts.Add(new("Author", raw.GetValueOrDefault("Author") ?? ""));
        }
        else
        {
            facts.Add(new("Location", app.InstallLocation));
            facts.Add(new("Package manager", app.PackageManager));
            facts.Add(new("Install source", app.InstallSource));
            facts.Add(app.PackageFullName.Length > 0 ? new("Package", app.PackageFullName) : new("Registry key", app.KeyPath));
        }
        facts.Add(new("Details", ev.ChangeType is ChangeType.Modified or ChangeType.Failed ? "" : ev.Details));
        Facts = facts.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();

        CanUninstall = ev.ChangeType != ChangeType.Removed &&
                       ev.Source is DetectionSource.Registry or DetectionSource.Store or DetectionSource.EventLog &&
                       EventActions.CanUninstall(app);
        CanOpenFolder = app.InstallLocation.Length > 0 && Directory.Exists(app.InstallLocation);
        CanExclude = _shell.CanModify;
        UninstallCommand = new AsyncCommand(async () =>
        {
            var message = await EventActions.UninstallAsync(app);
            if (message != null) _shell.ShowMessage(message);
        });
        OpenFolderCommand = new RelayCommand(() => Dialogs.OpenFolder(app.InstallLocation));
        ExcludeCommand = new AsyncCommand(() => _shell.ExcludeAsync(ev), () => _shell.CanModify);
        CopyDetailsCommand = new RelayCommand(() => { Dialogs.CopyText(EventActions.DetailsText(ev)); _shell.ShowMessage("Details copied"); });
        CopyRecordCommand = new RelayCommand(() => { Dialogs.CopyText(EventActions.ChangeRecordText(ev)); _shell.ShowMessage("Change record copied — paste it into the ticket"); });
        ToggleReviewedCommand = new RelayCommand(() =>
        {
            _shell.SetReviewed([ev], !Item.IsReviewed);
            OnPropertiesChanged(nameof(ReviewButtonText), nameof(AttentionBadge));
        });
    }

    public EventItemViewModel Item { get; }

    public bool CanUninstall { get; }
    public bool CanOpenFolder { get; }
    public bool CanExclude { get; }

    private static string SentenceFor(ChangeEvent ev)
    {
        if (ev.ChangeType != ChangeType.Modified) return Display.Sentence(ev);
        return ev.Source switch
        {
            DetectionSource.Service => "This service's configuration changed. The before and after values are below.",
            DetectionSource.Driver => "This driver's configuration changed. The before and after values are below.",
            DetectionSource.ScheduledTask => "This task's action or account changed. The before and after values are below.",
            DetectionSource.EventLog => Display.Sentence(ev),
            _ => ev.PreviousApp != null
                ? "Its registration changed while the version stayed the same. The before and after values are below."
                : Display.Sentence(ev)
        };
    }

    public string Title => Item.Title;
    public string SubtitleLine { get; }
    public string Sentence { get; }
    public ToneBadge TypeBadge => Item.TypeBadge;

    public bool HasAttention => Item.IsFlagged || Item.Attention.Level == AttentionLevel.Notice;

    public ToneBadge? AttentionBadge => Item.Attention.Level switch
    {
        AttentionLevel.None => null,
        AttentionLevel.Notice => new ToneBadge(Item.Attention.Reason, Tone.Accent, Display.Glyphs.Info),
        _ => new ToneBadge(Item.IsReviewed ? $"{Item.Attention.Reason} — reviewed" : Item.Attention.Reason,
            Item.Attention.Level == AttentionLevel.Critical ? Tone.Critical : Tone.Caution, Display.Glyphs.Warning)
    };

    public bool CanReview => Item.IsFlagged;

    public string ReviewButtonText => Item.IsReviewed ? "Mark as not reviewed" : "Mark reviewed";

    // ── Diff ──────────────────────────────────────────────────────────────────

    public IReadOnlyList<DiffRow> AllDiffRows { get; }

    public bool HasDiff => AllDiffRows.Count > 0;

    public int UnchangedCount => AllDiffRows.Count(r => r.Kind == DiffKind.Same);

    public bool ShowUnchanged
    {
        get => _showUnchanged;
        set
        {
            if (Set(ref _showUnchanged, value)) OnPropertyChanged(nameof(DiffRows));
        }
    }

    public IReadOnlyList<DiffRow> DiffRows => _showUnchanged ? AllDiffRows : AllDiffRows.Where(r => r.Kind != DiffKind.Same).ToList();

    public string ShowUnchangedLabel => $"Show {UnchangedCount} unchanged value{(UnchangedCount == 1 ? "" : "s")}";

    // ── Facts and actions ────────────────────────────────────────────────────

    public IReadOnlyList<Fact> Facts { get; }

    public ICommand UninstallCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ExcludeCommand { get; }
    public ICommand CopyDetailsCommand { get; }
    public ICommand CopyRecordCommand { get; }
    public ICommand ToggleReviewedCommand { get; }
}
