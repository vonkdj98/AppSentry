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
    private readonly TaskbarIcon _icon;
    private BrandIcon.State _state = (BrandIcon.State)(-1);
    private System.Drawing.Icon? _trayIcon;

    public TrayService(ShellViewModel shell, Action showWindow, Action<long> showEvent, Action exit)
    {
        _shell = shell;
        _showWindow = showWindow;
        _showEvent = showEvent;
        _exit = exit;

        var menu = new ContextMenu();
        menu.Opened += (_, _) => BuildMenu(menu);
        BuildMenu(menu);

        _icon = new TaskbarIcon
        {
            ToolTipText = "AppSentry",
            ContextMenu = menu,
            LeftClickCommand = new RelayCommand(_showWindow),
            NoLeftClickDelay = true
        };
        Refresh();
        _icon.ForceCreate(false); // false: don't put the process into Windows efficiency mode
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
            _icon.Icon = _trayIcon;
            previous?.Dispose();
        }

        var tip = "AppSentry";
        if (needsLook > 0) tip += $" · {needsLook} need{(needsLook == 1 ? "s" : "")} a look";
        if (paused) tip += " · notifications paused";
        if (_shell.Status.IsScanning) tip += " · scanning";
        _icon.ToolTipText = tip;
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
        _icon.Dispose();
        _trayIcon?.Dispose();
    }
}
