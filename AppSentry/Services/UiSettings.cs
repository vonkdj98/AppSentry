using System.IO;
using AppSentry.Core.Backend;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Services;

/// <summary>
/// Per-user UI preferences (%APPDATA%\AppSentry\ui.json). Engine settings — interval, real-time,
/// exclusions — live in the engine's database instead, because the service shares them.
/// </summary>
public sealed class UiSettings
{
    public string Theme { get; set; } = "System"; // System, Light, Dark

    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Change types that pop a notification. Updates are off by default: they're routine and frequent.</summary>
    public HashSet<ChangeType> NotifyTypes { get; set; } = [ChangeType.Installed, ChangeType.Removed, ChangeType.Modified, ChangeType.Failed];

    /// <summary>Anything that "needs a look" notifies even if its type is switched off above.</summary>
    public bool AlwaysNotifyAttention { get; set; } = true;

    public bool NotificationSound { get; set; }

    /// <summary>How long a notification stays on screen. Until dismissed matches v1's default popup.</summary>
    public NotificationOnScreen OnScreen { get; set; } = NotificationOnScreen.UntilDismissed;

    /// <summary>Critical items (security tool removed, service binary swapped) stay on screen until dismissed,
    /// even when <see cref="OnScreen"/> lets other notifications go.</summary>
    public bool KeepCriticalOnScreen { get; set; } = true;

    public DateTime? NotificationsPausedUntilUtc { get; set; }

    public bool CloseToTray { get; set; } = true;

    public bool CloseToTrayTipShown { get; set; }

    /// <summary>Attention items this user has marked as reviewed (they stop counting toward "Needs a look").</summary>
    public HashSet<long> ReviewedEventIds { get; set; } = [];

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public bool NotificationsPaused => NotificationsPausedUntilUtc is { } until && until > DateTime.UtcNow;
}

public enum NotificationOnScreen
{
    UntilDismissed, // reminder scenario: stays until clicked or closed
    Long,           // about 25 seconds
    WindowsDefault  // Windows' own timeout (Accessibility > Visual effects, 5 s unless changed)
}

public static class UiSettingsStore
{
    private static string Dir => LocalBackend.DefaultDataDir;
    private static string PathOf => System.IO.Path.Combine(Dir, "ui.json");

    /// <summary>Demo and screenshot modes keep settings in memory so sample data never touches the real ui.json.</summary>
    public static bool InMemoryOnly { get; set; }

    public static UiSettings Load()
    {
        if (InMemoryOnly) return new UiSettings();
        try
        {
            if (File.Exists(PathOf))
                return AppJson.Deserialize<UiSettings>(File.ReadAllText(PathOf)) ?? new UiSettings();
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"ui.json unreadable, using defaults: {ex.Message}");
        }
        return MigrateFromV1();
    }

    /// <summary>Writes to a temp file and swaps it in, so a crash can't leave half a file.</summary>
    public static void Save(UiSettings settings)
    {
        if (InMemoryOnly) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var temp = PathOf + ".tmp";
            File.WriteAllText(temp, AppJson.Serialize(settings));
            File.Move(temp, PathOf, overwrite: true);
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Could not save ui.json: {ex.Message}");
        }
    }

    /// <summary>First run of v2: carry over the v1 theme, sound and notification auto-hide preferences.</summary>
    private static UiSettings MigrateFromV1()
    {
        var settings = new UiSettings();
        try
        {
            var theme = System.IO.Path.Combine(Dir, "theme.txt");
            if (File.Exists(theme))
            {
                var value = File.ReadAllText(theme).Trim();
                if (value is "System" or "Light" or "Dark") settings.Theme = value;
            }
            var sound = System.IO.Path.Combine(Dir, "sound.txt");
            if (File.Exists(sound)) settings.NotificationSound = File.ReadAllText(sound).Trim() == "1";
            // v1 stored auto-hide seconds: 0 = stay until dismissed, else 10, 30 or 60.
            var hide = System.IO.Path.Combine(Dir, "notifyhide.txt");
            if (File.Exists(hide) && int.TryParse(File.ReadAllText(hide).Trim(), out var seconds))
                settings.OnScreen = seconds switch
                {
                    <= 0 => NotificationOnScreen.UntilDismissed,
                    < 25 => NotificationOnScreen.WindowsDefault,
                    _ => NotificationOnScreen.Long
                };
        }
        catch
        {
            // Defaults are fine.
        }
        return settings;
    }
}
