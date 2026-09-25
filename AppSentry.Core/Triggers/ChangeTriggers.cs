using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using AppSentry.Core.Sources;
using AppSentry.Core.Util;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AppSentry.Core.Triggers;

/// <summary>A registry key to watch: which hive/view, the path, and what kind of change counts.</summary>
public sealed record RegistryWatch(RegistryHive Hive, string Path, bool Subtree, bool ValuesToo, string Label);

/// <summary>
/// Real-time change signals. None of these detect anything themselves — they only tell the
/// engine "something changed, scan soon" (the engine debounces). Detection stays in the scan,
/// so a missed or spurious signal can never create or lose an event.
///
///  - Registry: RegNotifyChangeKeyValue on the uninstall keys, AppX repository keys, the
///    Services root and the TaskCache tree, on one dedicated thread.
///  - Event log: EventLogWatcher for MsiInstaller events.
///  - Folders: FileSystemWatcher on each watched root.
/// </summary>
public sealed class ChangeTriggers : IDisposable
{
    private const uint RegNotifyChangeName = 0x1;
    private const uint RegNotifyChangeLastSet = 0x4;

    private readonly Action<string> _onChange;
    private readonly object _lock = new();
    private readonly AutoResetEvent _refresh = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _registryThread;
    private readonly List<FileSystemWatcher> _folderWatchers = [];
    private EventLogWatcher? _msiWatcher;
    private List<RegistryWatch> _registryWatches = [];
    private List<string> _folders = [];
    private bool _disposed;

    public ChangeTriggers(Action<string> onChange)
    {
        _onChange = onChange;
        _registryThread = new Thread(RegistryLoop) { IsBackground = true, Name = "AppSentry registry triggers" };
        _registryThread.Start();
        StartMsiWatcher();
    }

    /// <summary>Replaces the watch set (users sign in and out, so this changes over time).</summary>
    public void Update(IReadOnlyList<RegistryWatch> registry, IReadOnlyList<string> folders)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (!registry.SequenceEqual(_registryWatches))
            {
                _registryWatches = registry.ToList();
                _refresh.Set();
            }
            if (!folders.SequenceEqual(_folders, StringComparer.OrdinalIgnoreCase))
            {
                _folders = folders.ToList();
                RebuildFolderWatchers();
            }
        }
    }

    /// <summary>The standard watch set for this engine context.</summary>
    public static List<RegistryWatch> DefaultRegistryWatches(Engine.EngineContext context)
    {
        var watches = new List<RegistryWatch>
        {
            new(RegistryHive.LocalMachine, RegistryPaths.Uninstall, true, true, "uninstall key"),
            new(RegistryHive.LocalMachine, RegistryPaths.Uninstall32, true, true, "uninstall key (32-bit)"),
            new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services", false, false, "services"),
            new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree", true, false, "scheduled tasks")
        };

        string[] sids;
        try { sids = Registry.Users.GetSubKeyNames().Where(Engine.EngineContext.IsUserSid).ToArray(); }
        catch { sids = []; }

        foreach (var sid in sids)
        {
            if (!context.IsElevated && !sid.Equals(context.UserSid, StringComparison.OrdinalIgnoreCase)) continue;
            watches.Add(new(RegistryHive.Users, $@"{sid}\{RegistryPaths.Uninstall}", true, true, "per-user uninstall key"));
            watches.Add(new(RegistryHive.Users,
                $@"{sid}_Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
                false, false, "Store packages"));
        }
        return watches;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Set();
        _registryThread.Join(TimeSpan.FromSeconds(5));
        foreach (var w in _folderWatchers) w.Dispose();
        _folderWatchers.Clear();
        _msiWatcher?.Dispose();
        _refresh.Dispose();
        _stop.Dispose();
    }

    // ── Registry ──────────────────────────────────────────────────────────────

    private sealed class Registration(RegistryWatch watch, RegistryKey key, AutoResetEvent signal) : IDisposable
    {
        public RegistryWatch Watch { get; } = watch;
        public RegistryKey Key { get; } = key;
        public AutoResetEvent Signal { get; } = signal;

        public void Dispose()
        {
            Key.Dispose();
            Signal.Dispose();
        }
    }

    private void RegistryLoop()
    {
        var registrations = new List<Registration>();
        try
        {
            while (true)
            {
                // WaitAny handles at most 64 handles: stop + refresh + 62 keys.
                var handles = new WaitHandle[] { _stop, _refresh }.Concat(registrations.Select(r => r.Signal)).Take(64).ToArray();
                var index = WaitHandle.WaitAny(handles);
                if (index == 0) return;
                if (index == 1)
                {
                    foreach (var r in registrations) r.Dispose();
                    registrations = Register();
                    continue;
                }

                var fired = registrations[index - 2];
                _onChange(fired.Watch.Label);
                if (!Arm(fired))
                {
                    // Key deleted (e.g. user hive unloaded): drop it until the next refresh.
                    fired.Dispose();
                    registrations.RemoveAt(index - 2);
                }
            }
        }
        catch (Exception ex)
        {
            EngineLog.Error("Registry trigger thread stopped", ex);
        }
        finally
        {
            foreach (var r in registrations) r.Dispose();
        }
    }

    private List<Registration> Register()
    {
        List<RegistryWatch> watches;
        lock (_lock) watches = _registryWatches.ToList();

        var registrations = new List<Registration>();
        foreach (var watch in watches.Take(62))
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(watch.Hive, watch.Hive == RegistryHive.Users ? RegistryView.Default : RegistryView.Registry64);
                var key = baseKey.OpenSubKey(watch.Path, writable: false);
                if (key == null) continue; // doesn't exist (yet); the interval scan still covers it
                var registration = new Registration(watch, key, new AutoResetEvent(false));
                if (Arm(registration)) registrations.Add(registration);
                else registration.Dispose();
            }
            catch (Exception)
            {
                // Not readable in this context (e.g. TaskCache as a standard user): skip it.
            }
        }
        EngineLog.Info($"Watching {registrations.Count} registry key(s) for changes");
        return registrations;
    }

    private static bool Arm(Registration r)
    {
        var filter = RegNotifyChangeName | (r.Watch.ValuesToo ? RegNotifyChangeLastSet : 0);
        return RegNotifyChangeKeyValue(r.Key.Handle, r.Watch.Subtree, filter, r.Signal.SafeWaitHandle, fAsynchronous: true) == 0;
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, uint dwNotifyFilter,
        SafeWaitHandle hEvent, bool fAsynchronous);

    // ── Event log ─────────────────────────────────────────────────────────────

    private void StartMsiWatcher()
    {
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName, MsiEventSource.XPathFilter(""));
            _msiWatcher = new EventLogWatcher(query);
            _msiWatcher.EventRecordWritten += (_, e) =>
            {
                e.EventRecord?.Dispose();
                _onChange("Windows Installer event");
            };
            _msiWatcher.Enabled = true;
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Event log trigger unavailable: {ex.Message}");
            _msiWatcher = null;
        }
    }

    // ── Folders ───────────────────────────────────────────────────────────────

    private void RebuildFolderWatchers()
    {
        foreach (var w in _folderWatchers) w.Dispose();
        _folderWatchers.Clear();
        foreach (var folder in _folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false
                };
                watcher.Created += (_, _) => _onChange("new folder");
                watcher.Deleted += (_, _) => _onChange("folder removed");
                watcher.Renamed += (_, _) => _onChange("folder renamed");
                watcher.Error += (_, _) => _onChange("folder watcher overflow");
                watcher.EnableRaisingEvents = true;
                _folderWatchers.Add(watcher);
            }
            catch (Exception ex)
            {
                EngineLog.Warn($"Cannot watch {folder}: {ex.Message}");
            }
        }
    }
}
