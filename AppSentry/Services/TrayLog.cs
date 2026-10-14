using System.IO;

namespace AppSentry.Services;

/// <summary>
/// The tray app's own log (%LOCALAPPDATA%\AppSentry\tray.log): when it starts, why it hides or exits, and every
/// exception nothing handled. The engine's log doesn't cover this: when the tray app talks to the service the engine
/// runs in the service, so an app that closes by itself used to leave nothing behind. Small, rolled, and free of
/// anything about the user's apps.
/// </summary>
public static class TrayLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Lock = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppSentry", "tray.log");

    public static string FilePath => Path;

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // A log that can't be written must never be the reason the app fails.
        }
    }

    /// <summary>The first few frames above the caller, to say who asked (a menu item, a window closing, a restart).</summary>
    public static string Caller()
    {
        try
        {
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames()
                .Select(f => f.GetMethod()).Where(m => m != null).Take(4)
                .Select(m => $"{m!.DeclaringType?.Name}.{m.Name}");
            return string.Join(" <- ", frames);
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
