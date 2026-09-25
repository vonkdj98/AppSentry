namespace AppSentry.Core.Util;

/// <summary>
/// Tiny append-only diagnostic log (engine.log in the data directory, rotated at 1 MB).
/// Exists so that failures inside a scan are recorded somewhere instead of vanishing.
/// </summary>
public static class EngineLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Lock = new();
    private static string? _path;

    public static string? LogPath => _path;

    public static void Initialize(string dataDir) => _path = Path.Combine(dataDir, "engine.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var path = _path;
        if (path == null) return;
        try
        {
            lock (Lock)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the engine down.
        }
    }
}
