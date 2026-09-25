using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.RegularExpressions;
using AppSentry.Core.Engine;
using AppSentry.Core.Util;

namespace AppSentry.Core.Sources;

public enum MsiKind
{
    Installed,
    Removed,
    Reconfigured, // repair / feature change
    Patched,
    Failed
}

/// <summary>One Windows Installer operation from the Application log.</summary>
public sealed record MsiEvent
{
    public long RecordId { get; init; }
    public DateTime TimeUtc { get; init; }
    public int EventId { get; init; }
    public MsiKind Kind { get; init; }
    public string ProductName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string ProductCode { get; init; } = "";
    public string UserName { get; init; } = "";
    public int? Status { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>Where the reader left off. Persisted, so installs made while AppSentry was closed are caught up.</summary>
public sealed record MsiBookmark
{
    public bool Initialized { get; init; }
    public long LastRecordId { get; init; }
    public DateTime LastTimeUtc { get; init; }
}

/// <summary>
/// Reads MsiInstaller events with <see cref="EventLogReader"/> and an XPath filter, starting
/// after the persisted record id (v1 started from "now" on every launch and walked the whole
/// legacy EventLog.Entries collection).
///
/// Event layout (1033/1034/1035): [0] product name, [1] version, [2] language, [3] status,
/// [4] manufacturer. Status 0 is success — v1 ignored it and logged failed installs as installs.
/// </summary>
public static partial class MsiEventSource
{
    private const string LogName = "Application";
    private const int MaxEventsPerScan = 2000;

    private static readonly int[] EventIds = [1022, 1033, 1034, 1035, 11707, 11708, 11724, 11725, 11728, 11729];

    public static string XPathFilter(string extra) =>
        $"*[System[Provider[@Name='MsiInstaller'] and ({string.Join(" or ", EventIds.Select(id => $"EventID={id}"))}){extra}]]";

    public static (List<MsiEvent> Events, MsiBookmark Next) Read(MsiBookmark? bookmark, Dictionary<string, string> health)
    {
        try
        {
            var latest = LatestRecord();

            // First run: start from the end of the log. History before AppSentry existed is not a "change".
            if (bookmark is not { Initialized: true })
            {
                health["Event log"] = "ok";
                return ([], new MsiBookmark
                {
                    Initialized = true,
                    LastRecordId = latest?.Id ?? 0,
                    LastTimeUtc = latest?.TimeUtc ?? DateTime.UtcNow
                });
            }

            // Record ids only go backwards when the log was cleared; fall back to time.
            var cleared = latest != null && latest.Value.Id < bookmark.LastRecordId;
            var filter = cleared
                ? $" and TimeCreated[@SystemTime>='{bookmark.LastTimeUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']"
                : $" and EventRecordID>{bookmark.LastRecordId}";

            var raw = new List<MsiEvent>();
            long maxId = cleared ? 0 : bookmark.LastRecordId;
            var maxTime = bookmark.LastTimeUtc;

            using (var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, XPathFilter(filter))))
            {
                for (var record = reader.ReadEvent(); record != null && raw.Count < MaxEventsPerScan; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        if (record.RecordId is { } rid && rid > maxId) maxId = rid;
                        var time = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
                        if (time > maxTime) maxTime = time;
                        var parsed = Parse(record, time);
                        if (parsed != null) raw.Add(parsed);
                    }
                }
            }

            // Nothing matched after a clear: advance to the current end so we don't re-scan by time forever.
            if (cleared && maxId == 0) maxId = latest!.Value.Id;

            health["Event log"] = cleared ? "ok (log was cleared; resumed by time)" : "ok";
            return (Deduplicate(raw), new MsiBookmark { Initialized = true, LastRecordId = maxId, LastTimeUtc = maxTime });
        }
        catch (Exception ex)
        {
            // Keep the old bookmark: the next scan retries from the same place.
            health["Event log"] = $"failed: {ex.Message}";
            EngineLog.Error("MSI event log read failed", ex);
            return ([], bookmark ?? new MsiBookmark());
        }
    }

    private static (long Id, DateTime TimeUtc)? LatestRecord()
    {
        using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName) { ReverseDirection = true });
        using var record = reader.ReadEvent();
        if (record?.RecordId is not { } id) return null;
        return (id, record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^\s*Product:\s*(?<name>.+?)\s+--?\s+(?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex ProductMessage();

    [GeneratedRegex(@"Update '(?<update>[^']+)'")]
    private static partial Regex UpdateName();

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"<Binary>(?<hex>[0-9A-Fa-f]+)</Binary>")]
    private static partial Regex BinaryElement();

    internal static MsiEvent? Parse(EventRecord record, DateTime timeUtc)
    {
        var props = record.Properties.Select(p => p.Value?.ToString()?.Trim() ?? "").ToList();
        var user = record.UserId is { } sid ? EngineContext.ResolveAccountName(sid) : "";
        var productCode = ProductCodeFrom(record);
        var id = record.Id;

        var ev = new MsiEvent
        {
            RecordId = record.RecordId ?? 0,
            TimeUtc = timeUtc,
            EventId = id,
            UserName = user,
            ProductCode = productCode
        };

        switch (id)
        {
            case 1033 or 1034 or 1035:
            {
                var status = props.Count > 3 && int.TryParse(props[3], out var s) ? s : (int?)null;
                var ok = status is null or 0;
                return ev with
                {
                    ProductName = props.ElementAtOrDefault(0) ?? "",
                    Version = props.ElementAtOrDefault(1) ?? "",
                    Manufacturer = props.ElementAtOrDefault(4) ?? "",
                    Status = status,
                    Kind = !ok ? MsiKind.Failed : id switch { 1033 => MsiKind.Installed, 1034 => MsiKind.Removed, _ => MsiKind.Reconfigured },
                    Detail = !ok
                        ? $"{(id == 1034 ? "Removal" : id == 1035 ? "Reconfiguration" : "Installation")} failed with status {status}"
                        : ""
                };
            }

            case 11707 or 11708 or 11724 or 11725 or 11728 or 11729:
            {
                var match = ProductMessage().Match(props.ElementAtOrDefault(0) ?? "");
                if (!match.Success) return null;
                var kind = id switch
                {
                    11707 => MsiKind.Installed,
                    11724 => MsiKind.Removed,
                    11728 => MsiKind.Reconfigured,
                    _ => MsiKind.Failed
                };
                return ev with
                {
                    ProductName = match.Groups["name"].Value.Trim(),
                    Kind = kind,
                    Detail = kind == MsiKind.Failed ? match.Groups["rest"].Value.Trim().TrimEnd('.') : ""
                };
            }

            case 1022:
            {
                // "Product: X - Update 'Y' installed successfully."
                var text = string.Join(" ", props);
                var match = ProductMessage().Match(props.ElementAtOrDefault(0) ?? "");
                var name = match.Success ? match.Groups["name"].Value.Trim() : props.ElementAtOrDefault(0) ?? "";
                var update = UpdateName().Match(text);
                return string.IsNullOrWhiteSpace(name) ? null : ev with
                {
                    ProductName = name,
                    Kind = MsiKind.Patched,
                    Detail = update.Success ? $"Patch installed: {update.Groups["update"].Value}" : "Patch installed"
                };
            }
        }
        return null;
    }

    /// <summary>MsiInstaller puts the ProductCode in the event's binary data as text.</summary>
    private static string ProductCodeFrom(EventRecord record)
    {
        try
        {
            var hex = BinaryElement().Match(record.ToXml());
            if (!hex.Success) return "";
            var bytes = Convert.FromHexString(hex.Groups["hex"].Value);
            var text = Encoding.ASCII.GetString(bytes.Where(b => b != 0).ToArray());
            var guid = GuidPattern().Match(text);
            return guid.Success ? guid.Value.ToUpperInvariant() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 1033 and 11707 (etc.) describe the same operation. Merge them per product and kind,
    /// keeping 103x's version/manufacturer/status. Different kinds are never merged, so an
    /// install followed by an uninstall in one window are both kept.
    /// </summary>
    private static List<MsiEvent> Deduplicate(List<MsiEvent> events)
    {
        var merged = new List<MsiEvent>();
        // Exact names, not normalized ones: "VC++ X64 Runtime" and "VC++ X86 Runtime" are two products.
        foreach (var group in events.GroupBy(e => (
                     Key: e.ProductCode.Length > 0 ? e.ProductCode : e.ProductName.ToLowerInvariant(),
                     e.Kind)))
        {
            var ordered = group.OrderBy(e => e.EventId < 2000 ? 0 : 1).ThenByDescending(e => e.TimeUtc).ToList();
            var best = ordered[0];
            merged.Add(best with
            {
                ProductCode = ordered.Select(e => e.ProductCode).FirstOrDefault(c => c.Length > 0) ?? "",
                UserName = ordered.Select(e => e.UserName).FirstOrDefault(u => u.Length > 0) ?? "",
                Detail = ordered.Select(e => e.Detail).FirstOrDefault(d => d.Length > 0) ?? "",
                TimeUtc = group.Max(e => e.TimeUtc)
            });
        }

        // A record without a ProductCode that duplicates one with a code (same exact name and kind) is dropped.
        var coded = merged.Where(e => e.ProductCode.Length > 0).ToList();
        return merged
            .Where(e => e.ProductCode.Length > 0 || !coded.Any(c =>
                c.Kind == e.Kind && c.ProductName.Equals(e.ProductName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.TimeUtc)
            .ToList();
    }
}
