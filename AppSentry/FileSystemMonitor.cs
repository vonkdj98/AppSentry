namespace AppSentry;

/// <summary>
/// Watches C:\Program Files and C:\Program Files (x86) for new folder creation.
/// Catches portable drops and installs that don't register in the registry.
/// Uses FileSystemWatcher for real-time detection plus periodic diff scanning.
/// </summary>
public class FileSystemMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _knownFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FolderChange> _pendingChanges = [];
    private readonly object _lock = new();

    private static readonly string[] WatchPaths =
    [
        @"C:\Program Files",
        @"C:\Program Files (x86)"
    ];

    public FileSystemMonitor()
    {
        // Take initial snapshot of existing folders
        foreach (var path in WatchPaths)
        {
            if (Directory.Exists(path))
            {
                foreach (var dir in Directory.GetDirectories(path))
                    _knownFolders.Add(dir);
            }
        }

        // Set up watchers for real-time detection
        foreach (var path in WatchPaths)
        {
            if (!Directory.Exists(path)) continue;

            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnFolderCreated;
                watcher.Deleted += OnFolderDeleted;
                watcher.Renamed += OnFolderRenamed;

                _watchers.Add(watcher);
            }
            catch (Exception)
            {
                // May fail if path is not accessible
            }
        }
    }

    private void OnFolderCreated(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            var folderName = Path.GetFileName(e.FullPath);
            if (ShouldIgnoreFolder(folderName)) return;

            _pendingChanges.Add(new FolderChange
            {
                FolderPath = e.FullPath,
                FolderName = folderName,
                ChangeType = FolderChangeType.Created,
                DetectedAt = DateTime.Now
            });
            _knownFolders.Add(e.FullPath);
        }
    }

    private void OnFolderDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            var folderName = Path.GetFileName(e.FullPath);
            if (ShouldIgnoreFolder(folderName)) return;

            _pendingChanges.Add(new FolderChange
            {
                FolderPath = e.FullPath,
                FolderName = folderName,
                ChangeType = FolderChangeType.Deleted,
                DetectedAt = DateTime.Now
            });
            _knownFolders.Remove(e.FullPath);
        }
    }

    private void OnFolderRenamed(object sender, RenamedEventArgs e)
    {
        lock (_lock)
        {
            _knownFolders.Remove(e.OldFullPath);
            _knownFolders.Add(e.FullPath);

            var folderName = Path.GetFileName(e.FullPath);
            if (ShouldIgnoreFolder(folderName)) return;

            _pendingChanges.Add(new FolderChange
            {
                FolderPath = e.FullPath,
                FolderName = folderName,
                ChangeType = FolderChangeType.Created,
                DetectedAt = DateTime.Now,
                Note = $"Renamed from {Path.GetFileName(e.OldFullPath)}"
            });
        }
    }

    /// <summary>
    /// Called during each scan to check for folder changes that the watcher may have missed
    /// (e.g. if the watcher buffer overflowed). Also returns any pending watcher events.
    /// </summary>
    public List<FolderChange> GetChangesAndResync()
    {
        var changes = new List<FolderChange>();

        lock (_lock)
        {
            // Drain pending watcher events
            changes.AddRange(_pendingChanges);
            _pendingChanges.Clear();
        }

        // Periodic resync: check for new/removed folders not caught by watcher
        var currentFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in WatchPaths)
        {
            if (!Directory.Exists(path)) continue;
            foreach (var dir in Directory.GetDirectories(path))
                currentFolders.Add(dir);
        }

        lock (_lock)
        {
            // New folders not in our known set (and not already reported by watcher)
            foreach (var folder in currentFolders)
            {
                if (!_knownFolders.Contains(folder))
                {
                    var folderName = Path.GetFileName(folder);
                    if (ShouldIgnoreFolder(folderName)) continue;

                    // Only add if not already in the pending changes we just drained
                    if (!changes.Any(c => c.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    {
                        changes.Add(new FolderChange
                        {
                            FolderPath = folder,
                            FolderName = folderName,
                            ChangeType = FolderChangeType.Created,
                            DetectedAt = DateTime.Now,
                            Note = "Detected via resync"
                        });
                    }
                    _knownFolders.Add(folder);
                }
            }

            // Removed folders
            var removed = _knownFolders.Where(f => !currentFolders.Contains(f)).ToList();
            foreach (var folder in removed)
            {
                var folderName = Path.GetFileName(folder);
                if (ShouldIgnoreFolder(folderName)) continue;

                if (!changes.Any(c => c.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                {
                    changes.Add(new FolderChange
                    {
                        FolderPath = folder,
                        FolderName = folderName,
                        ChangeType = FolderChangeType.Deleted,
                        DetectedAt = DateTime.Now,
                        Note = "Detected via resync"
                    });
                }
                _knownFolders.Remove(folder);
            }
        }

        return changes;
    }

    /// <summary>
    /// Filters out system/temp folders that aren't real app installs.
    /// </summary>
    private static bool ShouldIgnoreFolder(string folderName)
    {
        var lower = folderName.ToLowerInvariant();
        var ignorePrefixes = new[]
        {
            "windows", "microsoft", "common files", "internet explorer",
            "msbuild", "reference assemblies", "dotnet", "iis",
            "windowspowershell", "windows defender", "windows mail",
            "windows media player", "windows multimedia platform",
            "windows nt", "windows photo viewer", "windows portable devices",
            "windows security", "windows sidebar", "uninstall information",
            "installshield installation information", "package cache"
        };

        foreach (var prefix in ignorePrefixes)
        {
            if (lower.StartsWith(prefix)) return true;
        }

        // Skip temp/hidden folders
        if (lower.StartsWith(".") || lower.StartsWith("_") || lower.StartsWith("$"))
            return true;

        return false;
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }
}

/// <summary>
/// Represents a detected folder creation or deletion in Program Files.
/// </summary>
public class FolderChange
{
    public string FolderPath { get; set; } = "";
    public string FolderName { get; set; } = "";
    public FolderChangeType ChangeType { get; set; }
    public DateTime DetectedAt { get; set; }
    public string Note { get; set; } = "";
}

public enum FolderChangeType
{
    Created,
    Deleted
}
