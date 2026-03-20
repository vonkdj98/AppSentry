using System.Diagnostics;
using AppTracker.Models;

namespace AppTracker;

/// <summary>
/// Monitors Windows Event Log for software install/uninstall events.
/// Catches silent installs by domain admins, GPO pushes, SCCM/Intune, etc.
///
/// Key event sources:
///   - MsiInstaller Event ID 11707 = install success
///   - MsiInstaller Event ID 11724 = uninstall success
///   - MsiInstaller Event ID 11707 with "reconfigured" = update/repair
///   - MsiInstaller Event ID 1022  = MSI patch applied
///   - MsiInstaller Event ID 1033  = install completed (detailed)
/// </summary>
public class EventLogMonitor
{
    private DateTime _lastCheckTime;
    private readonly object _lock = new();

    public EventLogMonitor()
    {
        _lastCheckTime = DateTime.Now;
    }

    /// <summary>
    /// Checks the Application event log for new MSI install/uninstall events
    /// since the last check. Returns change events not already captured by
    /// the registry scanner.
    /// </summary>
    public List<EventLogInstall> CheckForNewEvents()
    {
        var results = new List<EventLogInstall>();
        DateTime checkFrom;

        lock (_lock)
        {
            checkFrom = _lastCheckTime;
            _lastCheckTime = DateTime.Now;
        }

        try
        {
            using var log = new EventLog("Application");

            // Read entries in reverse (newest first), stop when we pass our window
            for (int i = log.Entries.Count - 1; i >= 0; i--)
            {
                try
                {
                    var entry = log.Entries[i];

                    // Stop once we're past our time window
                    if (entry.TimeGenerated < checkFrom)
                        break;

                    // Only look at MsiInstaller events
                    if (!entry.Source.Equals("MsiInstaller", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var eventId = entry.InstanceId & 0xFFFF; // mask off qualifier bits

                    switch (eventId)
                    {
                        case 11707: // Install success
                            results.Add(ParseInstallEvent(entry, EventLogChangeType.Installed));
                            break;
                        case 11724: // Uninstall success
                            results.Add(ParseInstallEvent(entry, EventLogChangeType.Removed));
                            break;
                        case 1033:  // Install completed (detailed, backup)
                            results.Add(Parse1033Event(entry));
                            break;
                    }
                }
                catch (Exception)
                {
                    // Skip unreadable entries
                }
            }
        }
        catch (Exception)
        {
            // Event log not accessible — needs admin or specific permissions
        }

        // Deduplicate by product name (11707 and 1033 can fire for same install)
        return results
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ProductName))
            .GroupBy(r => r.ProductName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList()!;
    }

    /// <summary>
    /// Parses Event ID 11707 (install success) or 11724 (uninstall success).
    /// Message format: "Product: [name] -- Installation operation [completed/failed] successfully."
    /// or "Product: [name] -- Removal operation [completed] successfully."
    /// </summary>
    private static EventLogInstall ParseInstallEvent(EventLogEntry entry, EventLogChangeType defaultType)
    {
        var message = entry.Message ?? "";
        var productName = "";
        var changeType = defaultType;

        // Extract product name: "Product: ProductName -- ..."
        var productIdx = message.IndexOf("Product:", StringComparison.OrdinalIgnoreCase);
        if (productIdx >= 0)
        {
            var afterProduct = message[(productIdx + 8)..].Trim();
            var dashIdx = afterProduct.IndexOf("--");
            if (dashIdx > 0)
                productName = afterProduct[..dashIdx].Trim();
            else
                productName = afterProduct.Trim().TrimEnd('.');
        }

        // Check if it's a reconfiguration (update)
        if (message.Contains("reconfigured", StringComparison.OrdinalIgnoreCase))
            changeType = EventLogChangeType.Updated;

        return new EventLogInstall
        {
            ProductName = productName,
            ChangeType = changeType,
            UserName = entry.UserName ?? "SYSTEM",
            TimeGenerated = entry.TimeGenerated,
            EventId = (int)(entry.InstanceId & 0xFFFF),
            RawMessage = message
        };
    }

    /// <summary>
    /// Parses Event ID 1033 — detailed install completion.
    /// Contains: product name, version, language, user who initiated, etc.
    /// </summary>
    private static EventLogInstall Parse1033Event(EventLogEntry entry)
    {
        var message = entry.Message ?? "";
        var productName = "";
        var version = "";

        // 1033 messages have replacement strings
        if (entry.ReplacementStrings is { Length: > 0 })
        {
            // Typical layout: [0]=product name, [1]=version, [2]=language, [3]=status, [4]=user
            if (entry.ReplacementStrings.Length > 0)
                productName = entry.ReplacementStrings[0];
            if (entry.ReplacementStrings.Length > 1)
                version = entry.ReplacementStrings[1];
        }

        // Fallback: parse from message text
        if (string.IsNullOrWhiteSpace(productName))
        {
            var lines = message.Split('\n');
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("Product Name:", StringComparison.OrdinalIgnoreCase))
                    productName = line[(line.IndexOf(':') + 1)..].Trim();
                if (line.TrimStart().StartsWith("Product Version:", StringComparison.OrdinalIgnoreCase))
                    version = line[(line.IndexOf(':') + 1)..].Trim();
            }
        }

        return new EventLogInstall
        {
            ProductName = productName,
            Version = version,
            ChangeType = EventLogChangeType.Installed,
            UserName = entry.UserName ?? "SYSTEM",
            TimeGenerated = entry.TimeGenerated,
            EventId = 1033,
            RawMessage = message
        };
    }
}

/// <summary>
/// Represents an install/uninstall event found in the Windows Event Log.
/// </summary>
public class EventLogInstall
{
    public string ProductName { get; set; } = "";
    public string Version { get; set; } = "";
    public EventLogChangeType ChangeType { get; set; }
    public string UserName { get; set; } = "";
    public DateTime TimeGenerated { get; set; }
    public int EventId { get; set; }
    public string RawMessage { get; set; } = "";
}

public enum EventLogChangeType
{
    Installed,
    Updated,
    Removed
}
