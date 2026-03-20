using System.Text.Json;
using System.Text.Json.Serialization;
using AppSentry.Models;

namespace AppSentry;

/// <summary>
/// Persists and queries the exclusion list in %APPDATA%\AppSentry\exclusions.json.
/// </summary>
internal class ExclusionStore
{
    private static readonly string ExclusionsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppSentry", "exclusions.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private List<ExclusionEntry> _entries = [];

    public IReadOnlyList<ExclusionEntry> Entries => _entries;

    public ExclusionStore()
    {
        Load();
    }

    /// <summary>True if this app name should be completely excluded from the change log.</summary>
    public bool IsExcludedFromLogging(string appName) =>
        _entries.Any(e => e.ExcludeLogging &&
            e.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if this app name should be excluded from notifications (popup/sound).
    /// Also returns true if excluded from logging (which implies no notifications).</summary>
    public bool IsExcludedFromNotifications(string appName) =>
        _entries.Any(e => (e.ExcludeNotifications || e.ExcludeLogging) &&
            e.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the exclusion entry for an app name, or null if not excluded.</summary>
    public ExclusionEntry? GetEntry(string appName) =>
        _entries.FirstOrDefault(e =>
            e.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));

    public void Add(ExclusionEntry entry)
    {
        // Remove existing entry for same app name before adding
        _entries.RemoveAll(e =>
            e.AppName.Equals(entry.AppName, StringComparison.OrdinalIgnoreCase));
        _entries.Add(entry);
        _entries.Sort((a, b) => string.Compare(a.AppName, b.AppName, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Remove(string appName)
    {
        _entries.RemoveAll(e =>
            e.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Update(ExclusionEntry entry)
    {
        Remove(entry.AppName);
        Add(entry);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(ExclusionsPath)) return;
            var json = File.ReadAllText(ExclusionsPath);
            _entries = JsonSerializer.Deserialize<List<ExclusionEntry>>(json, JsonOptions) ?? [];
        }
        catch { _entries = []; }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ExclusionsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_entries, JsonOptions);
            File.WriteAllText(ExclusionsPath, json);
        }
        catch { }
    }
}
