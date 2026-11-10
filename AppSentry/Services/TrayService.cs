using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppSentry.Core.Detection;
using AppSentry.Infrastructure;
using AppSentry.ViewModels;
using H.NotifyIcon;

namespace AppSentry.Services;

/// <summary>
/// Tray icon and menu. The icon shows state (a dot when something needs a look, a pause badge
/// while notifications are paused); the menu lists the latest changes and can pause notifications.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Action _showWindow;
    private readonly Action<long> _showEvent;
    private readonly Action _exit;
    private readonly ContextMenu _menu;
    private TaskbarIcon _icon;
    private DateTime _lastRebuildUtc = DateTime.MinValue;
    private bool _failing;
    private BrandIcon.State _state = (BrandIcon.State)(-1);
    private System.Drawing.Icon? _trayIcon;
    private IReadOnlyList<string> _editionLines = [];
    private readonly System.Windows.Threading.DispatcherTimer? _tipTimer;
    private bool _fetching;

    /// <summary>Windows' limit for a tray tooltip (szTip holds 128 characters with the terminator).</summary>
    public const int MaxTip = 127;

    public TrayService(ShellViewModel shell, Action showWindow, Action<long> showEvent, Action exit)
    {
        _shell = shell;
        _showWindow = showWindow;
        _showEvent = showEvent;
        _exit = exit;

        _menu = new ContextMenu();
        _menu.Opened += (_, _) => BuildMenu(_menu);
        BuildMenu(_menu);

        _state = BrandIcon.State.Normal;
        _trayIcon = BrandIcon.TrayIcon(_state);
        _icon = CreateIcon();
        Refresh();

        // An edition's lines (Guard: speed in and out) change all the time; keep them fresh so hovering shows now.
        if (_shell.EditionTrayLines != null)
        {
            _tipTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _tipTimer.Tick += async (_, _) => await RefreshEditionLinesAsync();
            _tipTimer.Start();
            _ = RefreshEditionLinesAsync();
        }
    }

    private async Task RefreshEditionLinesAsync()
    {
        if (_fetching || _shell.EditionTrayLines is not { } lines) return;
        _fetching = true;
        try
        {
            _editionLines = await lines();
        }
        catch (Exception)
        {
            _editionLines = []; // the service is restarting, or the edition isn't licensed
        }
        finally
        {
            _fetching = false;
        }
        UpdateTip();
    }

    /// <summary>"AppSentry · Monitoring", the edition's lines, then what needs attention; cut to Windows' limit.</summary>
    public static string BuildTip(string status, IReadOnlyList<string> editionLines, int needsLook, bool paused)
    {
        // AppSentry's own lines are kept whole; an edition's lines go in between, as many as fit.
        var head = $"AppSentry · {status}";
        var tail = new List<string>();
        if (needsLook > 0) tail.Add($"{needsLook} change{(needsLook == 1 ? " needs" : "s need")} a look");
        if (paused) tail.Add("Notifications paused");
        var lines = new List<string> { head };
        var used = head.Length + tail.Sum(t => t.Length + 1);
        foreach (var line in editionLines.Where(l => l.Length > 0))
        {
            var room = MaxTip - used - 1;
            if (line.Length > room)
            {
                if (room > 1) lines.Add(line[..(room - 1)] + "…");
                break;
            }
            lines.Add(line);
            used += line.Length + 1;
        }
        lines.AddRange(tail);
        var tip = string.Join("\n", lines);
        return tip.Length <= MaxTip ? tip : tip[..(MaxTip - 1)] + "…";
    }

    private void UpdateTip() =>
        Apply(() => _icon.ToolTipText = BuildTip(_shell.StatusText, _editionLines, _shell.NeedsLookCount, _shell.Settings.NotificationsPaused));

    private TaskbarIcon CreateIcon()
    {
        var icon = new TaskbarIcon
        {
            ToolTipText = "AppSentry",
            Icon = _trayIcon,
            ContextMenu = _menu,
            LeftClickCommand = new RelayCommand(_showWindow),
            NoLeftClickDelay = true
        };
        try
        {
            icon.ForceCreate(false); // false: don't put the process into Windows efficiency mode
        }
        catch (Exception ex)
        {
            // The taskbar isn't ready (just after sign-in) or Explorer is restarting: the next update rebuilds it.
            TrayLog.Write($"Tray icon not created yet: {ex.Message}");
        }
        return icon;
    }

    /// <summary>
    /// Changes the tray icon, and never lets a failure out. Windows refuses changes to an icon it has lost (Explorer
    /// restarted, or the icon went up before the taskbar was ready), and the library then throws on every update:
    /// with Guard's tooltip refreshing every 3 seconds, that stacked up error boxes. A failure is logged once and the
    /// icon rebuilt, at most every 30 seconds, until Windows takes it again.
    /// </summary>
    private void Apply(Action change)
    {
        try
        {
            change();
            if (_failing) TrayLog.Write("Tray icon working again");
            _failing = false;
        }
        catch (Exception ex)
        {
            if (!_failing) TrayLog.Write($"Tray icon update failed ({ex.Message}); rebuilding it");
            _failing = true;
            if (DateTime.UtcNow - _lastRebuildUtc < TimeSpan.FromSeconds(30)) return;
            _lastRebuildUtc = DateTime.UtcNow;
            try { _icon.Dispose(); } catch (Exception) { /* already gone from the shell */ }
            _icon = CreateIcon();
        }
    }

    /// <summary>Re-evaluates icon and tooltip; call when counts, pause or status change.</summary>
    public void Refresh()
    {
        var paused = _shell.Settings.NotificationsPaused;
        var needsLook = _shell.NeedsLookCount;
        var state = paused ? BrandIcon.State.Paused : needsLook > 0 ? BrandIcon.State.Attention : BrandIcon.State.Normal;
        if (state != _state)
        {
            _state = state;
            var previous = _trayIcon;
            _trayIcon = BrandIcon.TrayIcon(state);
            Apply(() => _icon.Icon = _trayIcon);
            previous?.Dispose();
        }

        UpdateTip();
    }

    private void BuildMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Item("Open AppSentry", Display.Glyphs.Shield, _showWindow, bold: true));
        menu.Items.Add(new Separator());

        var recent = _shell.AllEvents.Take(5).ToList();
        menu.Items.Add(new MenuItem { Header = recent.Count == 0 ? "No changes yet" : "Recent changes", IsEnabled = false });
        foreach (var ev in recent)
        {
            var attention = AttentionClassifier.Classify(ev);
            var label = $"{Display.TypeLabel(ev.ChangeType)}  ·  {Display.Shorten(Display.CleanName(ev.App.Name), 40)}  ·  {Display.ShortTime(ev.EffectiveTime)}";
            var glyph = attention.Level >= AttentionLevel.Warning ? Display.Glyphs.Warning : Display.Glyph(ev.ChangeType);
            var id = ev.Id;
            menu.Items.Add(Item(label, glyph, () => _showEvent(id)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Scan now", Display.Glyphs.Refresh, () => _shell.ScanCommand.Execute(null)));

        if (_shell.Settings.NotificationsPaused)
        {
            var until = Display.ToLocal(_shell.Settings.NotificationsPausedUntilUtc!.Value);
            menu.Items.Add(Item($"Resume notifications (paused until {until:HH:mm})", Display.Glyphs.Pause, () => _shell.PauseNotifications(null)));
        }
        else
        {
            var pause = new MenuItem { Header = "Pause notifications", Icon = Glyph(Display.Glyphs.Pause) };
            pause.Items.Add(Item("For 1 hour", null, () => _shell.PauseNotifications(TimeSpan.FromHours(1))));
            pause.Items.Add(Item("For 4 hours", null, () => _shell.PauseNotifications(TimeSpan.FromHours(4))));
            pause.Items.Add(Item("Until tomorrow", null, () => _shell.PauseNotifications(DateTime.Today.AddDays(1).AddHours(8) - DateTime.Now)));
            menu.Items.Add(pause);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", null, _exit));
    }

    private static MenuItem Item(string header, string? glyph, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header, Icon = glyph == null ? null : Glyph(glyph) };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center
    };

    public void Dispose()
    {
        _tipTimer?.Stop();
        _icon.Dispose();
        _trayIcon?.Dispose();
    }
}
