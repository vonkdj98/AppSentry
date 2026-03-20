using AppSentry.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppSentry;

/// <summary>
/// Persists the registry snapshot and change history to %APPDATA%\AppSentry\.
/// Uses System.Text.Json (no external dependencies).
/// </summary>
public class SnapshotStore
{
    private readonly string _dataDir;
    private readonly string _snapshotPath;
    private readonly string _historyPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SnapshotStore()
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AppSentry");
        Directory.CreateDirectory(_dataDir);
        _snapshotPath = Path.Combine(_dataDir, "snapshot.json");
        _historyPath = Path.Combine(_dataDir, "history.json");
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────

    public Dictionary<string, InstalledApp>? LoadSnapshot()
    {
        if (!File.Exists(_snapshotPath)) return null;
        try
        {
            var json = File.ReadAllText(_snapshotPath);
            return JsonSerializer.Deserialize<Dictionary<string, InstalledApp>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveSnapshot(Dictionary<string, InstalledApp> snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(_snapshotPath, json);
    }

    // ── History ───────────────────────────────────────────────────────────────

    public List<ChangeEvent> LoadHistory()
    {
        if (!File.Exists(_historyPath)) return [];
        try
        {
            var json = File.ReadAllText(_historyPath);
            return JsonSerializer.Deserialize<List<ChangeEvent>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void AppendHistory(List<ChangeEvent> newEvents)
    {
        if (newEvents.Count == 0) return;
        var history = LoadHistory();
        history.AddRange(newEvents);
        var json = JsonSerializer.Serialize(history, JsonOptions);
        File.WriteAllText(_historyPath, json);
    }

    public void ClearHistory()
    {
        if (File.Exists(_historyPath))
            File.Delete(_historyPath);
    }

    public string DataDirectory => _dataDir;
}
