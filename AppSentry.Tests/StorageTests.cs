using AppSentry.Core.Storage;
using AppSentry.Models;

namespace AppSentry.Tests;

public class StorageTests
{
    private static ChangeEvent Event(string name, ChangeType type, DateTime at) => new()
    {
        App = new InstalledApp { KeyPath = $@"HKLM\X\{name}", Name = name, Version = "1.0" },
        ChangeType = type,
        DetectedAt = at
    };

    [Fact]
    public void Commit_assigns_ids_and_round_trips_events_and_state()
    {
        using var dir = new TempDir();
        var store = new SqliteStore(dir.Path);
        var t0 = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        var saved = store.Commit(
            [Event("A", ChangeType.Installed, t0), Event("B", ChangeType.Removed, t0.AddMinutes(1))],
            new Dictionary<string, object?> { ["k"] = new List<string> { "x" } });

        Assert.All(saved, e => Assert.True(e.Id > 0));
        var loaded = store.LoadEvents();
        Assert.Equal(["B", "A"], loaded.Select(e => e.App.Name)); // newest first
        Assert.Equal(DateTimeKind.Utc, loaded[0].DetectedAt.Kind);
        Assert.Equal(["x"], store.GetState<List<string>>("k"));
    }

    [Fact]
    public void Garbage_database_is_quarantined_not_overwritten()
    {
        using var dir = new TempDir();
        var db = dir.File("appsentry.db");
        File.WriteAllText(db, "this is not a database, it is someone's history");

        var store = new SqliteStore(dir.Path);

        Assert.NotNull(store.RecoveryNotice);
        var quarantined = Directory.GetFiles(dir.Path, "appsentry.db.corrupt-*").Single(f => !f.EndsWith("-wal") && !f.EndsWith("-shm"));
        Assert.Contains("someone's history", File.ReadAllText(quarantined));
        Assert.Empty(store.LoadEvents()); // fresh, usable database
    }

    [Fact]
    public void Legacy_history_is_imported_once_and_converted_to_utc()
    {
        using var dir = new TempDir();
        // v1 wrote DateTime.Now with an offset and the positional-record property names.
        File.WriteAllText(dir.File("history.json"), """
            [{"App":{"KeyPath":"HKLM\\X\\Old","Name":"Old App","Version":"1.2","Publisher":"","InstallDate":"",
              "InstallLocation":"","InstalledBy":"All Users (Admin)","InstallSource":"","InstallType":"MSI"},
              "ChangeType":"Installed","PreviousVersion":null,"DetectedAt":"2026-03-30T10:00:00-04:00","Source":"Registry"}]
            """);
        File.WriteAllText(dir.File("exclusions.json"), """[{"AppName":"Noisy","ExcludeNotifications":true,"ExcludeLogging":false}]""");
        File.WriteAllText(dir.File("interval.txt"), "3");

        var store = new SqliteStore(dir.Path);
        LegacyImporter.ImportIfNeeded(store, dir.Path);
        LegacyImporter.ImportIfNeeded(store, dir.Path); // second call is a no-op

        var ev = Assert.Single(store.LoadEvents());
        Assert.Equal("Old App", ev.App.Name);
        Assert.Equal(new DateTime(2026, 3, 30, 14, 0, 0, DateTimeKind.Utc), ev.DetectedAt.ToUniversalTime());
        Assert.Equal("Noisy", Assert.Single(store.GetState<List<ExclusionEntry>>(StateKeys.Exclusions)!).AppName);
        Assert.Equal(30, store.GetState<EngineSettings>(StateKeys.Settings)!.ScanIntervalMinutes);
        Assert.True(File.Exists(dir.File("history.json.imported")));
    }

    [Fact]
    public void Unreadable_legacy_history_is_left_in_place()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("history.json"), "[{ truncated");

        var store = new SqliteStore(dir.Path);
        LegacyImporter.ImportIfNeeded(store, dir.Path);

        Assert.True(File.Exists(dir.File("history.json")));
        Assert.Empty(store.LoadEvents());
    }
}

public class HistoryPagingTests
{
    private static ChangeEvent Registry(string name, DateTime at) => new()
    {
        App = new InstalledApp { KeyPath = $@"HKLM\X\{name}", Name = name, Version = "2.0", RawValues = new() { ["DisplayVersion"] = "2.0", ["Big"] = new string('x', 500) } },
        PreviousApp = new InstalledApp { KeyPath = $@"HKLM\X\{name}", Name = name, Version = "1.0", RawValues = new() { ["DisplayVersion"] = "1.0" } },
        PreviousVersion = "1.0",
        ChangeType = ChangeType.Updated,
        DetectedAt = at,
        Source = DetectionSource.Registry
    };

    [Fact]
    public void Pages_walk_newest_first_without_gaps_or_repeats()
    {
        using var dir = new TempDir();
        var store = new SqliteStore(dir.Path);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Commit(Enumerable.Range(0, 25).Select(i => Registry($"App{i}", t0.AddMinutes(i))).ToList(), new Dictionary<string, object?>());

        var seen = new List<long>();
        long? before = null;
        bool more;
        do
        {
            var page = store.LoadEventsPage(before, 10, out more);
            seen.AddRange(page.Select(e => e.Id));
            before = page.Count > 0 ? page[^1].Id : null;
        } while (more);

        Assert.Equal(25, seen.Count);
        Assert.Equal(seen.OrderByDescending(i => i), seen);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public void Slim_pages_drop_app_raw_values_but_keep_service_values()
    {
        var app = MonitorEngineSlim(Registry("Tool", DateTime.UtcNow));
        Assert.Null(app.App.RawValues);
        Assert.Null(app.PreviousApp!.RawValues);
        Assert.Equal("1.0", app.PreviousVersion);

        var service = new ChangeEvent
        {
            App = new InstalledApp { Name = "[Windows Service] X", RawValues = new() { ["ImagePath"] = @"C:\x.exe" } },
            ChangeType = ChangeType.Installed,
            Source = DetectionSource.Service
        };
        Assert.NotNull(MonitorEngineSlim(service).App.RawValues); // the "needs a look" rules read these
    }

    [Fact]
    public void Retention_deletes_only_older_events()
    {
        using var dir = new TempDir();
        var store = new SqliteStore(dir.Path);
        var now = DateTime.UtcNow;
        store.Commit([Registry("Old", now.AddDays(-400)), Registry("Recent", now.AddDays(-10))], new Dictionary<string, object?>());

        Assert.Equal(1, store.DeleteEventsBefore(now.AddDays(-365)));
        Assert.Equal("Recent", Assert.Single(store.LoadEvents()).App.Name);
    }

    private static ChangeEvent MonitorEngineSlim(ChangeEvent ev) => AppSentry.Core.Engine.MonitorEngine.Slim(ev);
}
