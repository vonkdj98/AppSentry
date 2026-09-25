using AppSentry.Core.Util;
using AppSentry.Models;
using Microsoft.Data.Sqlite;

namespace AppSentry.Core.Storage;

/// <summary>
/// SQLite persistence for change history and engine state (snapshot, baselines, bookmarks,
/// exclusions, settings). Replaces the v1 JSON files, which were rewritten in full on every
/// append and could be wiped by a single bad read.
///
/// Guarantees:
///  - A scan's events and the state they were derived from are written in one transaction
///    (<see cref="Commit"/>), so a crash can never persist a bookmark without its events.
///  - A file that isn't a readable database is moved aside, never overwritten.
/// </summary>
public sealed class SqliteStore
{
    private readonly string _connectionString;

    public string DbPath { get; }

    /// <summary>Set when the database had to be quarantined on open.</summary>
    public string? RecoveryNotice { get; private set; }

    public SqliteStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        DbPath = Path.Combine(dataDir, "appsentry.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        try
        {
            InitializeSchema();
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            Quarantine(ex);
            InitializeSchema();
        }
    }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>All events, newest first.</summary>
    public List<ChangeEvent> LoadEvents() => QueryEvents("SELECT id, json FROM events ORDER BY detected_at DESC, id DESC", null);

    public List<ChangeEvent> LoadEventsSince(DateTime utc) =>
        QueryEvents("SELECT id, json FROM events WHERE detected_at >= $since ORDER BY detected_at DESC, id DESC",
            cmd => cmd.Parameters.AddWithValue("$since", ToKey(utc)));

    public int CountEvents()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void ClearEvents()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM events";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts events and upserts state keys in a single transaction. Returns the events
    /// with their database ids assigned. A null state value deletes that key.
    /// </summary>
    public List<ChangeEvent> Commit(IReadOnlyList<ChangeEvent> events, IReadOnlyDictionary<string, object?> state)
    {
        var saved = new List<ChangeEvent>(events.Count);
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO events (detected_at, change_type, source, name, json) VALUES ($at, $type, $source, $name, $json); " +
                "SELECT last_insert_rowid();";
            var pAt = insert.Parameters.Add("$at", SqliteType.Text);
            var pType = insert.Parameters.Add("$type", SqliteType.Text);
            var pSource = insert.Parameters.Add("$source", SqliteType.Text);
            var pName = insert.Parameters.Add("$name", SqliteType.Text);
            var pJson = insert.Parameters.Add("$json", SqliteType.Text);

            foreach (var ev in events)
            {
                pAt.Value = ToKey(ev.DetectedAt);
                pType.Value = ev.ChangeType.ToString();
                pSource.Value = ev.Source.ToString();
                pName.Value = ev.App.Name;
                pJson.Value = AppJson.Serialize(ev with { Id = 0 }); // the row id is authoritative; see QueryEvents
                var id = Convert.ToInt64(insert.ExecuteScalar());
                saved.Add(ev with { Id = id });
            }
        }

        foreach (var (key, value) in state)
            WriteState(conn, tx, key, value);

        tx.Commit();
        return saved;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    public T? GetState<T>(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM state WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteScalar() is not string json) return default;
        try
        {
            return AppJson.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            EngineLog.Error($"State key '{key}' is unreadable and will be ignored", ex);
            return default;
        }
    }

    public void SetState(string key, object? value)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        WriteState(conn, tx, key, value);
        tx.Commit();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS events (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                detected_at TEXT NOT NULL,
                change_type TEXT NOT NULL,
                source      TEXT NOT NULL,
                name        TEXT NOT NULL,
                json        TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_detected_at ON events (detected_at);
            CREATE TABLE IF NOT EXISTS state (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        var result = check.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new SqliteException($"quick_check failed: {result}", 11);
    }

    private static void WriteState(SqliteConnection conn, SqliteTransaction tx, string key, object? value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (value == null)
        {
            cmd.CommandText = "DELETE FROM state WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
        }
        else
        {
            cmd.CommandText = "INSERT INTO state (key, value) VALUES ($key, $value) " +
                              "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value is string s ? s : AppJson.Serialize(value));
        }
        cmd.ExecuteNonQuery();
    }

    private List<ChangeEvent> QueryEvents(string sql, Action<SqliteCommand>? bind)
    {
        var events = new List<ChangeEvent>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            try
            {
                var ev = AppJson.Deserialize<ChangeEvent>(reader.GetString(1));
                if (ev != null) events.Add(ev with { Id = id });
            }
            catch (Exception ex)
            {
                // One bad row must not hide the rest of the history.
                EngineLog.Error($"Event row {id} is unreadable and was skipped", ex);
            }
        }
        return events;
    }

    /// <summary>Sortable UTC key: 2026-09-25T14:03:11.1234567Z.</summary>
    private static string ToKey(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime())
        .ToString("O");

    private static bool IsCorruption(SqliteException ex) =>
        ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */;

    private void Quarantine(SqliteException ex)
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = $"{DbPath}.corrupt-{stamp}";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(DbPath + suffix)) File.Move(DbPath + suffix, target + suffix);
            }
            catch (Exception moveEx)
            {
                EngineLog.Error($"Could not move aside {DbPath}{suffix}", moveEx);
            }
        }
        RecoveryNotice = $"The history database was unreadable and was moved to {Path.GetFileName(target)}. A new one was started.";
        EngineLog.Error(RecoveryNotice, ex);
    }
}
