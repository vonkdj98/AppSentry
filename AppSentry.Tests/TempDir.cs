using Microsoft.Data.Sqlite;

namespace AppSentry.Tests;

/// <summary>A throwaway directory under %TEMP% that is deleted when the test ends.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AppSentryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
