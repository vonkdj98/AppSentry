using AppSentry.Core.Detection;
using AppSentry.Core.Storage;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Engine;

/// <summary>
/// Owns every detection source, the scan schedule and persistence.
///
/// Scheduling rules:
///  - Only one scan runs at a time. A request that arrives mid-scan sets a "rescan pending"
///    flag and a follow-up scan runs right after, so no change is lost and no two scans ever
///    diff against the same baseline.
///  - Trigger-driven requests are debounced (trailing, capped) so an installer that writes
///    hundreds of keys produces one scan, not hundreds.
///  - A 30-second heartbeat drives the safety-net interval scan and catches sleep/resume.
/// </summary>
public sealed partial class MonitorEngine : IDisposable
{
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxDebounce = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResumeGap = TimeSpan.FromMinutes(2);

    private readonly EngineOptions _options;
    private readonly object _schedLock = new();
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    private Timer? _scanTimer;
    private Timer? _heartbeat;
    private DateTime? _dueAtUtc;
    private DateTime? _firstRequestUtc;
    private string _pendingReason = "";
    private bool _scanRunning;
    private bool _rescanPending;
    private DateTime _lastHeartbeatUtc = DateTime.UtcNow;
    private DateTime _lastScanCompletedUtc = DateTime.MinValue;
    private DateTime? _followUpDueUtc;
    private volatile bool _disposed;
    private bool _started;

    private EngineSettings _settings = new();
    private List<ExclusionEntry> _exclusionList = [];
    private ExclusionMatcher _exclusions = new([]);
    private EngineStatus _status;

    public MonitorEngine(EngineOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.DataDir);
        EngineLog.Initialize(options.DataDir);
        Store = new SqliteStore(options.DataDir);
        Context = EngineContext.Capture(options.Mode);
        _status = new EngineStatus { Mode = options.Mode == EngineMode.Service ? "Service" : "Local", Notice = Store.RecoveryNotice };
    }

    public SqliteStore Store { get; }

    public EngineContext Context { get; }

    public EngineStatus Status => _status;

    public EngineSettings Settings => _settings;

    /// <summary>Raised on the engine thread after new events are committed.</summary>
    public event EventHandler<IReadOnlyList<ChangeEvent>>? EventsDetected;

    /// <summary>Raised on the engine thread whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<EngineStatus>? StatusChanged;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;

        if (_options.LegacyDir != null)
        {
            try { LegacyImporter.ImportIfNeeded(Store, _options.LegacyDir); }
            catch (Exception ex) { EngineLog.Error("Legacy import failed", ex); }
        }

        _settings = Store.GetState<EngineSettings>(StateKeys.Settings) ?? new EngineSettings();
        _exclusionList = Store.GetState<List<ExclusionEntry>>(StateKeys.Exclusions) ?? [];
        _exclusions = new ExclusionMatcher(_exclusionList);
        LoadSourceState();

        EngineLog.Info($"Engine started ({Context.Describe()}), data in {_options.DataDir}");

        try { ApplyRetention(); }
        catch (Exception ex) { EngineLog.Error("Retention failed", ex); }

        _scanTimer = new Timer(_ => RunDueScan(), null, Timeout.Infinite, Timeout.Infinite);
        _heartbeat = new Timer(_ => Heartbeat(), null, HeartbeatPeriod, HeartbeatPeriod);
        OnStarted();
        RequestScan("startup", TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeat?.Dispose();
        _scanTimer?.Dispose();
        OnStopping();
        // Let an in-flight scan finish its transaction rather than tearing it down mid-write.
        if (!_idle.Wait(TimeSpan.FromSeconds(30)))
            EngineLog.Warn("Shut down while a scan was still running");
        EngineLog.Info("Engine stopped");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Schedules a scan. delay = 0 runs as soon as possible; a positive delay debounces
    /// (each new request pushes the scan out, but never past 60 s after the first one).
    /// </summary>
    public void RequestScan(string reason, TimeSpan delay)
    {
        lock (_schedLock)
        {
            if (_disposed || _scanTimer == null) return;
            var now = DateTime.UtcNow;
            _firstRequestUtc ??= now;

            DateTime due;
            if (delay <= TimeSpan.Zero)
            {
                due = now;
            }
            else if (_dueAtUtc is { } existing && existing <= now + TimeSpan.FromSeconds(1))
            {
                due = existing; // an immediate scan is already queued; don't push it out
            }
            else
            {
                due = now + delay;
                var cap = _firstRequestUtc.Value + MaxDebounce;
                if (due > cap) due = cap;
            }

            _dueAtUtc = due;
            _pendingReason = string.IsNullOrEmpty(_pendingReason) ? reason : _pendingReason;
            var wait = due - now;
            _scanTimer.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
        }
    }

    public List<ChangeEvent> GetHistory() => Store.LoadEvents();

    /// <summary>One page of history, newest first; slim pages leave out app events' raw registry values.</summary>
    public HistoryPage GetHistoryPage(HistoryQuery query)
    {
        var events = Store.LoadEventsPage(query.BeforeId, query.Limit, out var hasMore);
        return new HistoryPage
        {
            Events = query.Slim ? events.Select(Slim).ToList() : events,
            HasMore = hasMore,
            TotalCount = Store.CountEvents()
        };
    }

    public ChangeEvent? GetEvent(long id) => Store.LoadEvent(id);

    /// <summary>
    /// App events carry every value of the uninstall key before and after (the bulk of an event).
    /// Lists don't need them; the details pane fetches the full event. Service and task events keep
    /// theirs: they're small and the "needs a look" rules read them.
    /// </summary>
    public static ChangeEvent Slim(ChangeEvent ev)
    {
        if (ev.Source is DetectionSource.Service or DetectionSource.Driver or DetectionSource.ScheduledTask) return ev;
        if (ev.App.RawValues == null && ev.PreviousApp?.RawValues == null) return ev;
        return ev with
        {
            App = ev.App with { RawValues = null },
            PreviousApp = ev.PreviousApp == null ? null : ev.PreviousApp with { RawValues = null }
        };
    }

    // ── Retention ─────────────────────────────────────────────────────────────

    private DateTime _lastRetentionUtc = DateTime.MinValue;

    /// <summary>Deletes history older than the retention setting (if one is set). Returns how many were removed.</summary>
    public int ApplyRetention()
    {
        _lastRetentionUtc = DateTime.UtcNow;
        var days = _settings.RetentionDays;
        if (days <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var deleted = Store.DeleteEventsBefore(cutoff);
        if (deleted > 0)
        {
            EngineLog.Info($"Retention: deleted {deleted} change(s) older than {days} days");
            PublishStatus(s => s with { Notice = $"Deleted {deleted:N0} changes older than {days} days" });
        }
        return deleted;
    }

    public void ClearHistory()
    {
        Store.ClearEvents();
        EngineLog.Info("History cleared");
        PublishStatus(s => s with { Notice = "History cleared" });
    }

    public List<InstalledApp> GetInventory() => CurrentInventory();

    public IReadOnlyList<ExclusionEntry> GetExclusions() => _exclusionList;

    public void SetExclusions(IEnumerable<ExclusionEntry> entries)
    {
        var list = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.AppName))
            .GroupBy(e => e.AppName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { AppName = g.Key })
            .OrderBy(e => e.AppName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Store.SetState(StateKeys.Exclusions, list);
        _exclusionList = list;
        _exclusions = new ExclusionMatcher(list);
    }

    public void UpdateSettings(EngineSettings settings)
    {
        var clean = settings with
        {
            ScanIntervalMinutes = Math.Clamp(settings.ScanIntervalMinutes, 0, 24 * 60),
            RetentionDays = Math.Clamp(settings.RetentionDays, 0, 3650)
        };
        Store.SetState(StateKeys.Settings, clean);
        var realtimeChanged = clean.RealtimeEnabled != _settings.RealtimeEnabled;
        var retentionChanged = clean.RetentionDays != _settings.RetentionDays;
        _settings = clean;
        if (realtimeChanged) OnRealtimeSettingChanged();
        if (retentionChanged) ApplyRetention();
    }

    // ── Scheduling internals ──────────────────────────────────────────────────

    private void RunDueScan()
    {
        string reason;
        lock (_schedLock)
        {
            if (_disposed) return;
            if (_scanRunning)
            {
                _rescanPending = true;
                return;
            }
            _scanRunning = true;
            _idle.Reset();
            reason = _pendingReason;
            _pendingReason = "";
            _dueAtUtc = null;
            _firstRequestUtc = null;
        }

        try
        {
            ScanOnce(reason);
        }
        catch (Exception ex)
        {
            EngineLog.Error($"Scan ({reason}) failed", ex);
            PublishStatus(s => s with { IsScanning = false, LastError = $"{DateTime.Now:HH:mm:ss} scan failed: {ex.Message}" });
        }
        finally
        {
            bool again;
            lock (_schedLock)
            {
                _lastScanCompletedUtc = DateTime.UtcNow;
                again = _rescanPending;
                _rescanPending = false;
                _scanRunning = false;
                _idle.Set();
            }
            if (again && !_disposed) RequestScan("changes arrived during the last scan", TimeSpan.FromSeconds(2));
        }
    }

    private void Heartbeat()
    {
        try
        {
            var now = DateTime.UtcNow;
            var gap = now - _lastHeartbeatUtc;
            _lastHeartbeatUtc = now;

            // A heartbeat that arrives minutes late means the machine was asleep.
            if (gap > ResumeGap + HeartbeatPeriod)
                RequestScan("resumed from sleep", TimeSpan.FromSeconds(15));

            var interval = _settings.ScanIntervalMinutes;
            if (interval > 0 && now - _lastScanCompletedUtc >= TimeSpan.FromMinutes(interval))
                RequestScan("interval", TimeSpan.Zero);

            if (_settings.RetentionDays > 0 && now - _lastRetentionUtc > TimeSpan.FromDays(1))
                ApplyRetention();

            DateTime? followUp;
            lock (_schedLock) followUp = _followUpDueUtc;
            if (followUp is { } due && now >= due)
            {
                lock (_schedLock) _followUpDueUtc = null;
                RequestScan("follow-up", TimeSpan.Zero);
            }
        }
        catch (Exception ex)
        {
            EngineLog.Error("Heartbeat failed", ex);
        }
    }

    /// <summary>Asks for one more scan no later than <paramref name="dueUtc"/> (e.g. to confirm a pending folder).</summary>
    private void ScheduleFollowUp(DateTime dueUtc)
    {
        lock (_schedLock)
        {
            if (_followUpDueUtc == null || dueUtc < _followUpDueUtc) _followUpDueUtc = dueUtc;
        }
    }

    // ── Commit + publish ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies exclusions, persists events and source state in one transaction, then raises
    /// <see cref="EventsDetected"/>. Exclusions run first so a "don't log" app never touches disk.
    /// </summary>
    private List<ChangeEvent> CommitScan(List<ChangeEvent> events, Dictionary<string, object?> state)
    {
        var matcher = _exclusions;
        var kept = new List<ChangeEvent>(events.Count);
        foreach (var ev in events)
        {
            if (matcher.ExcludesLogging(ev.App.Name)) continue;
            kept.Add(matcher.ExcludesNotifications(ev.App.Name) ? ev with { Silent = true } : ev);
        }

        var saved = Store.Commit(kept, state);

        if (saved.Count > 0)
        {
            EngineLog.Info($"Recorded {saved.Count} change(s): " +
                string.Join("; ", saved.Take(10).Select(e => $"{e.ChangeType} {e.App.Name} [{e.Source}]")));
            try { EventsDetected?.Invoke(this, saved); }
            catch (Exception ex) { EngineLog.Error("EventsDetected subscriber threw", ex); }
        }
        return saved;
    }

    private void PublishStatus(Func<EngineStatus, EngineStatus> update)
    {
        EngineStatus next;
        lock (_schedLock)
        {
            next = update(_status);
            _status = next;
        }
        try { StatusChanged?.Invoke(this, next); }
        catch (Exception ex) { EngineLog.Error("StatusChanged subscriber threw", ex); }
    }

    // Hooks implemented by the scan pipeline partial.
    partial void OnStarted();
    partial void OnStopping();
    partial void OnRealtimeSettingChanged();
}
