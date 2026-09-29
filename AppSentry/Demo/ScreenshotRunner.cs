using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppSentry.Services;
using AppSentry.ViewModels;
using AppSentry.Views;

namespace AppSentry.Demo;

/// <summary>
/// --screenshots &lt;dir&gt; [--theme Light|Dark]: renders every page with <see cref="DemoBackend"/> data
/// to PNGs (off-screen), then exits. Used to review the design without a person at the screen.
/// </summary>
public static class ScreenshotRunner
{
    public static int Run(string dir, string theme, bool synthetic = false)
    {
        Directory.CreateDirectory(dir);
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var suffix = theme.ToLowerInvariant();

        app.Startup += async (_, _) =>
        {
            var code = 0;
            try
            {
                app.ApplyTheme(theme);
                var backend = new DemoBackend(synthetic);
                var shell = new ShellViewModel(backend, new UiSettings { Theme = theme });
                var window = new MainWindow(shell)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = 0,
                    Width = 1360,
                    Height = 860,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.UseSolidBackground();
                window.Show();
                await shell.InitializeAsync();

                async Task Shot(string name)
                {
                    await Settle();
                    Render(window, Path.Combine(dir, $"{name}-{suffix}.png"));
                }

                var events = shell.AllEvents;
                var flagged = events.FirstOrDefault(e => e.Source == Models.DetectionSource.Service);
                if (flagged != null) shell.Activity.Select(flagged.Id);
                await Shot("activity");

                var update = events.FirstOrDefault(e => e.ChangeType == Models.ChangeType.Updated);
                if (update != null) shell.Activity.Select(update.Id);
                await Shot("activity-update");

                shell.SelectedNav = shell.NavItems.First(n => n.Page == shell.Installed);
                await Settle();
                shell.Installed.SelectedSort = shell.Installed.SortOptions[1]; // by size shows the bars
                await Shot("installed");

                shell.SelectedNav = shell.NavItems.First(n => n.Page == shell.Persistence);
                await Shot("services");
                shell.Persistence.IsTasks = true;
                await Shot("tasks");

                shell.SelectedNav = shell.NavItems.First(n => n.Page == shell.Exclusions);
                await Shot("exclusions");

                // Any other page (added by an edition) gets a shot named after its title.
                var covered = new HashSet<object> { shell.Activity, shell.Installed, shell.Persistence, shell.Exclusions };
                foreach (var extra in shell.NavItems.Where(n => !covered.Contains(n.Page)).ToList())
                {
                    shell.SelectedNav = extra;
                    await Settle();
                    await Shot(extra.Title.ToLowerInvariant().Replace(' ', '-'));
                }

                shell.SelectedNav = shell.SettingsNav;
                await Shot("settings");

                foreach (var state in Enum.GetValues<BrandIcon.State>())
                    BrandIcon.SavePng(BrandIcon.Render(64, state), Path.Combine(dir, $"icon-{state.ToString().ToLowerInvariant()}.png"));
                BrandIcon.SavePng(BrandIcon.Render(16), Path.Combine(dir, "icon-16.png"));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
                code = 1;
            }
            finally
            {
                app.Shutdown(code);
            }
        };
        return app.Run();
    }

    private static async Task Settle()
    {
        // Let bindings, layout and background icon loads finish.
        for (var i = 0; i < 8; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(150);
        }
    }

    private static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        BrandIcon.SavePng(bitmap, path);
    }
}
