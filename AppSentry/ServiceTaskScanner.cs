using System.Diagnostics;
using System.ServiceProcess;

namespace AppSentry;

/// <summary>
/// Scans for newly registered Windows Services and Scheduled Tasks.
/// Catches domain admin stealth installs that register services or tasks
/// without traditional installer registry entries.
/// </summary>
public class ServiceTaskScanner
{
    private HashSet<string> _knownServices = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownTasks = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    /// <summary>
    /// Takes an initial snapshot of all services and scheduled tasks.
    /// Must be called once before CheckForChanges().
    /// </summary>
    public void Initialize()
    {
        _knownServices = GetCurrentServices();
        _knownTasks = GetCurrentScheduledTasks();
        _initialized = true;
    }

    /// <summary>
    /// Checks for new or removed services and scheduled tasks since last check.
    /// </summary>
    public List<ServiceTaskChange> CheckForChanges()
    {
        if (!_initialized)
        {
            Initialize();
            return []; // First run, just baseline
        }

        var changes = new List<ServiceTaskChange>();

        // ── Check Services ────────────────────────────────────────────────
        var currentServices = GetCurrentServices();

        // New services
        foreach (var svc in currentServices)
        {
            if (!_knownServices.Contains(svc))
            {
                var info = GetServiceInfo(svc);
                if (info != null && !IsSystemService(info))
                {
                    changes.Add(new ServiceTaskChange
                    {
                        Name = info.DisplayName,
                        ItemType = ServiceTaskType.Service,
                        ChangeType = ServiceTaskChangeType.Added,
                        Details = $"Service: {info.ServiceName} | Start: {info.StartType} | Path: {info.BinaryPath}",
                        DetectedAt = DateTime.Now
                    });
                }
            }
        }

        // Removed services
        foreach (var svc in _knownServices)
        {
            if (!currentServices.Contains(svc))
            {
                changes.Add(new ServiceTaskChange
                {
                    Name = svc,
                    ItemType = ServiceTaskType.Service,
                    ChangeType = ServiceTaskChangeType.Removed,
                    Details = "Service was removed",
                    DetectedAt = DateTime.Now
                });
            }
        }

        _knownServices = currentServices;

        // ── Check Scheduled Tasks ─────────────────────────────────────────
        var currentTasks = GetCurrentScheduledTasks();

        foreach (var task in currentTasks)
        {
            if (!_knownTasks.Contains(task) && !IsSystemTask(task))
            {
                changes.Add(new ServiceTaskChange
                {
                    Name = task,
                    ItemType = ServiceTaskType.ScheduledTask,
                    ChangeType = ServiceTaskChangeType.Added,
                    Details = "New scheduled task registered",
                    DetectedAt = DateTime.Now
                });
            }
        }

        foreach (var task in _knownTasks)
        {
            if (!currentTasks.Contains(task))
            {
                changes.Add(new ServiceTaskChange
                {
                    Name = task,
                    ItemType = ServiceTaskType.ScheduledTask,
                    ChangeType = ServiceTaskChangeType.Removed,
                    Details = "Scheduled task was removed",
                    DetectedAt = DateTime.Now
                });
            }
        }

        _knownTasks = currentTasks;

        return changes;
    }

    // ── Service enumeration ──────────────────────────────────────────────────

    private static HashSet<string> GetCurrentServices()
    {
        var services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var svc in ServiceController.GetServices())
            {
                services.Add(svc.ServiceName);
                svc.Dispose();
            }
        }
        catch (Exception) { }
        return services;
    }

    private static ServiceInfo? GetServiceInfo(string serviceName)
    {
        try
        {
            using var svc = new ServiceController(serviceName);
            var displayName = svc.DisplayName;
            var startType = svc.StartType.ToString();

            // Get binary path from registry (ServiceController doesn't expose it)
            var binaryPath = "";
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                binaryPath = key?.GetValue("ImagePath") as string ?? "";
            }
            catch { }

            return new ServiceInfo
            {
                ServiceName = serviceName,
                DisplayName = displayName,
                StartType = startType,
                BinaryPath = binaryPath
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Filters out core Windows services that aren't third-party installs.
    /// </summary>
    private static bool IsSystemService(ServiceInfo info)
    {
        var pathLower = info.BinaryPath.ToLowerInvariant();

        // Windows system services
        if (pathLower.Contains(@"\windows\system32\"))
            return true;
        if (pathLower.Contains(@"\windows\syswow64\"))
            return true;
        if (pathLower.StartsWith("\"c:\\windows\\"))
            return true;

        // Known Microsoft service prefixes
        var systemPrefixes = new[]
        {
            "wua", "wmi", "win", "wsearch", "spooler", "bits",
            "cryptsvc", "dnscache", "dhcp", "eventlog", "lanman",
            "netlogon", "plugplay", "rpcss", "samss", "schedule",
            "sens", "sharedaccess", "themes", "w32time"
        };

        var nameLower = info.ServiceName.ToLowerInvariant();
        foreach (var prefix in systemPrefixes)
        {
            if (nameLower.StartsWith(prefix)) return true;
        }

        return false;
    }

    // ── Scheduled Task enumeration ───────────────────────────────────────────

    private static HashSet<string> GetCurrentScheduledTasks()
    {
        var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /FO CSV /NH",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return tasks;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim().Trim('"');
                // First field is the task name
                var commaIdx = trimmed.IndexOf("\",\"");
                var taskName = commaIdx > 0 ? trimmed[..commaIdx] : trimmed;
                taskName = taskName.Trim('"').Trim();

                if (!string.IsNullOrWhiteSpace(taskName))
                    tasks.Add(taskName);
            }
        }
        catch (Exception) { }
        return tasks;
    }

    /// <summary>
    /// Filters out built-in Windows scheduled tasks.
    /// </summary>
    private static bool IsSystemTask(string taskName)
    {
        var lower = taskName.ToLowerInvariant();

        // Microsoft/Windows built-in task paths
        if (lower.StartsWith(@"\microsoft\"))
            return true;
        if (lower.StartsWith(@"\windows\"))
            return true;

        // Common system tasks
        var systemTasks = new[]
        {
            "microsoftedgeupdate", "googleupdate", "onedrive",
            "user_feed_synchronization", "adobe acrobat update",
            @"\createexploreshelluninstallkey"
        };

        foreach (var sys in systemTasks)
        {
            if (lower.Contains(sys)) return true;
        }

        return false;
    }
}

public class ServiceInfo
{
    public string ServiceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string StartType { get; set; } = "";
    public string BinaryPath { get; set; } = "";
}

public class ServiceTaskChange
{
    public string Name { get; set; } = "";
    public ServiceTaskType ItemType { get; set; }
    public ServiceTaskChangeType ChangeType { get; set; }
    public string Details { get; set; } = "";
    public DateTime DetectedAt { get; set; }
}

public enum ServiceTaskType
{
    Service,
    ScheduledTask
}

public enum ServiceTaskChangeType
{
    Added,
    Removed
}
