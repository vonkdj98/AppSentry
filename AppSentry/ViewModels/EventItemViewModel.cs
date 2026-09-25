using System.Windows.Media;
using AppSentry.Core.Detection;
using AppSentry.Infrastructure;
using AppSentry.Models;
using AppSentry.Services;

namespace AppSentry.ViewModels;

/// <summary>A colored label: change type, "needs a look", source health.</summary>
public sealed record ToneBadge(string Text, Tone Tone, string Glyph = "");

/// <summary>One row in the Activity list (and in an app's history).</summary>
public sealed class EventItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconRequested;
    private bool _isReviewed;

    public EventItemViewModel(ChangeEvent ev, bool isReviewed)
    {
        Event = ev;
        Attention = AttentionClassifier.Classify(ev);
        _isReviewed = isReviewed;
        Tone = Display.ToneFor(ev.ChangeType, Attention.Level);
        TypeBadge = new ToneBadge(Display.TypeLabel(ev.ChangeType), Display.ToneFor(ev.ChangeType, AttentionLevel.None), Display.Glyph(ev.ChangeType));
        DayLabel = Display.DayLabel(ev.EffectiveTime);
        SearchText = $"{ev.App.Name} {ev.App.Publisher} {ev.App.Version} {ev.PreviousVersion} {ev.ChangedBy} {ev.App.InstalledFor} {ev.App.InstallType} {ev.Details} {ev.App.PackageManager}".ToLowerInvariant();
    }

    public ChangeEvent Event { get; }
    public long Id => Event.Id;
    public Attention Attention { get; }
    public bool IsFlagged => Attention.Level >= AttentionLevel.Warning;

    public bool IsReviewed
    {
        get => _isReviewed;
        set
        {
            if (Set(ref _isReviewed, value)) OnPropertiesChanged(nameof(NeedsLook), nameof(FlagBadge));
        }
    }

    public bool NeedsLook => IsFlagged && !IsReviewed;

    public ToneBadge? FlagBadge => NeedsLook ? new ToneBadge("Needs a look", Tone, Display.Glyphs.Warning) : null;

    public string Title => Display.CleanName(Event.App.Name);
    public string Subtitle => Display.Summary(Event, Attention);
    public string Time => Display.ShortTime(Event.EffectiveTime);
    public string FullTime => Display.LocalTime(Event.EffectiveTime);
    public string DayLabel { get; }
    public Tone Tone { get; }
    public ToneBadge TypeBadge { get; }
    public string Glyph => IsFlagged ? Display.Glyphs.Warning : Display.Glyph(Event.ChangeType);
    public string SourceGlyph => Display.SourceGlyph(Event.Source);
    public string SearchText { get; }

    /// <summary>The app's real icon, loaded in the background the first time a row asks for it.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                if (AppIconCache.TryGetCached(Event.App, out var cached)) _icon = cached;
                else _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    public bool HasIcon => Icon != null;

    private async Task LoadIconAsync()
    {
        var icon = await AppIconCache.LoadAsync(Event.App);
        if (icon == null) return;
        _icon = icon;
        ShellViewModel.OnUi(() => OnPropertiesChanged(nameof(Icon), nameof(HasIcon)));
    }
}
