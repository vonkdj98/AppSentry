using System.Windows;
using System.Windows.Controls;
using AppSentry.Services;
using AppSentry.ViewModels;

namespace AppSentry.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        _shell = shell;
        DataContext = shell;
        Icon = BrandIcon.Render(32);
        BrandImage.Source = BrandIcon.Render(48);
        FitToScreen();
        ApplySavedBounds(shell.Settings);
    }

    /// <summary>The default size is for a roomy screen; on a small one (a 1366x768 laptop) the window stays inside it.</summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width * 0.96));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height * 0.96));
    }

    /// <summary>Screenshot mode renders the client area without Mica, so give it a solid base.</summary>
    public void UseSolidBackground() => Root.SetResourceReference(Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

    public void SaveBounds()
    {
        var s = _shell.Settings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.WindowLeft = bounds.Left;
        s.WindowTop = bounds.Top;
        s.WindowWidth = bounds.Width;
        s.WindowHeight = bounds.Height;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        _shell.SaveSettings();
    }

    private void ApplySavedBounds(UiSettings s)
    {
        if (s.WindowWidth is not { } w || s.WindowHeight is not { } h || s.WindowLeft is not { } l || s.WindowTop is not { } t) return;
        // Only restore if the window would be at least partly on a current screen.
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!virtualScreen.IntersectsWith(new Rect(l, t, w, h))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l;
        Top = t;
        Width = Math.Max(MinWidth, w);
        Height = Math.Max(MinHeight, h);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }
}
