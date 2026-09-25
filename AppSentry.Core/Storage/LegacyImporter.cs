using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Storage;

/// <summary>
/// One-time import of the v1 JSON files (history.json, exclusions.json, interval.txt) into SQLite.
/// The v1 snapshot.json is deliberately not imported: registry and Store key formats changed,
/// so the first v2 scan takes a fresh baseline instead of reporting a storm of false changes.
/// </summary>
public static class LegacyImporter
{
    public const string ImportedStateKey = "legacy.imported";

    public static void ImportIfNeeded(SqliteStore store, string legacyDir)
    {
        if (store.GetState<bool>(ImportedStateKey)) return;

        var state = new Dictionary<string, object?> { [ImportedStateKey] = true };
        var events = new List<ChangeEvent>();
        var renames = new List<string>();

        var historyPath = Path.Combine(legacyDir, "history.json");
        if (File.Exists(historyPath))
        {
            try
            {
                var old = AppJson.Deserialize<List<ChangeEvent>>(File.ReadAllText(historyPath)) ?? [];
                // v1 stored local times; v2 stores UTC. Oldest first so ids ascend with time.
                events.AddRange(old
                    .Select(e => e with { DetectedAt = ToUtc(e.DetectedAt) })
                    .OrderBy(e => e.DetectedAt));
                renames.Add(historyPath);
            }
            catch (Exception ex)
            {
                // Leave the file exactly where it is so nothing is lost; just don't retry forever.
                EngineLog.Error($"Could not import {historyPath}; the file was left untouched", ex);
            }
        }

        var exclusionsPath = Path.Combine(legacyDir, "exclusions.json");
        if (File.Exists(exclusionsPath))
        {
            try
            {
                state[StateKeys.Exclusions] = AppJson.Deserialize<List<ExclusionEntry>>(File.ReadAllText(exclusionsPath)) ?? [];
                renames.Add(exclusionsPath);
            }
            catch (Exception ex)
            {
                EngineLog.Error($"Could not import {exclusionsPath}; the file was left untouched", ex);
            }
        }

        var intervalPath = Path.Combine(legacyDir, "interval.txt");
        if (File.Exists(intervalPath) && int.TryParse(File.ReadAllText(intervalPath).Trim(), out var idx))
        {
            // v1 stored the combo index: 1, 5, 10, 30 minutes, Off.
            var minutes = idx switch { 0 => 1, 1 => 5, 2 => 10, 3 => 30, _ => 0 };
            state[StateKeys.Settings] = new EngineSettings { ScanIntervalMinutes = minutes };
            renames.Add(intervalPath);
        }

        store.Commit(events, state);

        foreach (var path in renames)
        {
            try { File.Move(path, path + ".imported", overwrite: true); }
            catch (Exception ex) { EngineLog.Warn($"Imported {path} but could not rename it: {ex.Message}"); }
        }

        if (events.Count > 0 || renames.Count > 0)
            EngineLog.Info($"Imported {events.Count} v1 history events from {legacyDir}");
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
    };
}
