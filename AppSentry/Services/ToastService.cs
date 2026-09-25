using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AppSentry.Core.Detection;
using AppSentry.Core.Util;
using AppSentry.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AppSentry.Services;

/// <summary>
/// Native Windows notifications (they respect Do Not Disturb, stay in Notification Center and
/// have buttons). Rules: per-type switches, "needs a look" always notifies, critical items stay
/// on screen until dismissed, pause for a while, excluded apps never notify.
/// </summary>
public sealed class ToastService
{
    private readonly Func<UiSettings> _settings;
    private readonly string _iconDir = Path.Combine(Path.GetTempPath(), "AppSentry", "toast-icons");

    public ToastService(Func<UiSettings> settings)
    {
        _settings = settings;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Notifications unavailable: {ex.Message}");
        }
    }

    /// <summary>Raised on the UI thread: ("view" | "exclude", event id or null).</summary>
    public event Action<string, long?>? Activated;

    public static bool ShouldNotify(ChangeEvent ev, UiSettings settings)
    {
        if (ev.Silent || !settings.NotificationsEnabled || settings.NotificationsPaused) return false;
        if (settings.NotifyTypes.Contains(ev.ChangeType)) return true;
        return settings.AlwaysNotifyAttention && AttentionClassifier.Classify(ev).Level >= AttentionLevel.Warning;
    }

    public async Task ShowAsync(IReadOnlyList<ChangeEvent> events)
    {
        var settings = _settings();
        var toShow = events.Where(e => ShouldNotify(e, settings)).ToList();
        if (toShow.Count == 0) return;

        try
        {
            var builder = new ToastContentBuilder().AddArgument("action", "view");
            var critical = false;

            if (toShow.Count == 1)
            {
                var ev = toShow[0];
                var attention = AttentionClassifier.Classify(ev);
                critical = attention.Level == AttentionLevel.Critical;
                builder.AddArgument("id", ev.Id)
                    .AddText(attention.Level >= AttentionLevel.Warning ? attention.Reason : Display.NotificationTitle(ev.ChangeType))
                    .AddText($"{Display.CleanName(ev.App.Name)}{(ev.App.Version.Length > 0 ? " " + ev.App.Version : "")}")
                    .AddText(Display.Summary(ev, Attention.None))
                    .AddButton(new ToastButton().SetContent("View").AddArgument("action", "view").AddArgument("id", ev.Id))
                    .AddButton(new ToastButton().SetContent("Exclude").AddArgument("action", "exclude").AddArgument("id", ev.Id));
                if (await AppIconFileAsync(ev.App) is { } icon)
                    builder.AddAppLogoOverride(new Uri(icon), ToastGenericAppLogoCrop.Default);
            }
            else
            {
                critical = toShow.Any(e => AttentionClassifier.Classify(e).Level == AttentionLevel.Critical);
                var flagged = toShow.Count(e => AttentionClassifier.Classify(e).Level >= AttentionLevel.Warning);
                builder.AddText(flagged > 0 ? $"{toShow.Count} changes, {flagged} need a look" : $"{toShow.Count} changes detected")
                    .AddText(string.Join(", ", toShow.Take(3).Select(e => Display.CleanName(e.App.Name))) + (toShow.Count > 3 ? ", …" : ""))
                    .AddButton(new ToastButton().SetContent("View").AddArgument("action", "view"));
            }

            if (!settings.NotificationSound) builder.AddAudio(new ToastAudio { Silent = true });
            if (critical && settings.KeepCriticalOnScreen) builder.SetToastScenario(ToastScenario.Reminder);

            builder.Show(toast =>
            {
                toast.Group = "changes";
                toast.ExpirationTime = DateTimeOffset.Now.AddDays(2);
            });
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Couldn't show a notification: {ex.Message}");
        }
    }

    public void ShowTest()
    {
        try
        {
            new ToastContentBuilder()
                .AddText("Notifications are working")
                .AddText("This is how AppSentry will tell you about changes.")
                .Show();
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Notifications", $"Windows didn't accept the notification: {ex.Message}");
        }
    }

    public void ShowInfo(string title, string body)
    {
        try
        {
            new ToastContentBuilder().AddText(title).AddText(body).AddAudio(new ToastAudio { Silent = true }).Show();
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Couldn't show a notification: {ex.Message}");
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        var action = args.TryGetValue("action", out var a) ? a : "view";
        long? id = args.TryGetValue("id", out var idText) && long.TryParse(idText, out var parsed) ? parsed : null;
        Application.Current?.Dispatcher.BeginInvoke(() => Activated?.Invoke(action, id));
    }

    /// <summary>Toasts need an image file; write the app's icon to a temp PNG (cached by name).</summary>
    private async Task<string?> AppIconFileAsync(InstalledApp app)
    {
        try
        {
            if (await AppIconCache.LoadAsync(app) is not BitmapSource image) return null;
            Directory.CreateDirectory(_iconDir);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(app.KeyPath)))[..16] + ".png";
            var path = Path.Combine(_iconDir, name);
            if (!File.Exists(path)) BrandIcon.SavePng(image, path);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
