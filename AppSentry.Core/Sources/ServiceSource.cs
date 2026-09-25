using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using AppSentry.Core.Engine;
using AppSentry.Models;
using Microsoft.Win32;

namespace AppSentry.Core.Sources;

public sealed record ServiceRecord
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ImagePath { get; init; } = "";
    public string ServiceDll { get; init; } = "";
    public int Start { get; init; }
    public int Type { get; init; }
    public string Account { get; init; } = "";
    public bool IsDriver { get; init; }

    /// <summary>Binary's CompanyName says Microsoft. Cached so file metadata is read once per binary.</summary>
    public bool IsMicrosoft { get; init; }
}

public sealed class ServiceBaseline
{
    public string Fingerprint { get; set; } = "";
    public Dictionary<string, ServiceRecord> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Windows services and drivers, baselined and persisted.
///
///  - The same "is this a Microsoft component?" rule filters additions and removals (v1 only
///    filtered additions, so Windows' own churn showed up as removals).
///  - Per-user service instances (CDPUserSvc_1a2b3 and friends), which Windows creates at
///    every sign-in and deletes at sign-out, are ignored.
///  - Changes to an existing service's binary, account or start type are reported as Modified —
///    that's what service hijacking and persistence look like.
/// </summary>
public static partial class ServiceSource
{
    private const int UserServiceInstanceFlag = 0x80;
    private const int UserServiceTemplateFlag = 0x40;

    [GeneratedRegex(@"^(?<template>.+)_[0-9a-f]{4,8}$", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceSuffix();

    public static (List<ChangeEvent> Events, ServiceBaseline Next) Scan(
        ServiceBaseline? baseline, EngineContext context, DateTime nowUtc, Dictionary<string, string> health)
    {
        Dictionary<string, ServiceRecord> current;
        try
        {
            current = Enumerate(baseline);
        }
        catch (Exception ex)
        {
            health["Services"] = $"failed: {ex.Message}";
            return ([], baseline ?? new ServiceBaseline());
        }

        var next = new ServiceBaseline { Fingerprint = context.Fingerprint, Services = current };
        health["Services"] = "ok";

        // First run, or now running as a different user/elevation: re-baseline silently.
        if (baseline == null || baseline.Fingerprint != context.Fingerprint)
            return ([], next);

        var events = new List<ChangeEvent>();
        foreach (var (name, svc) in current)
        {
            if (!baseline.Services.TryGetValue(name, out var before))
            {
                if (!svc.IsMicrosoft) events.Add(Event(svc, ChangeType.Installed, nowUtc, Describe(svc)));
                continue;
            }
            var changes = Changes(before, svc);
            if (changes.Count > 0) events.Add(Event(svc, ChangeType.Modified, nowUtc, string.Join("; ", changes), before));
        }
        foreach (var (name, before) in baseline.Services)
        {
            if (!current.ContainsKey(name) && !before.IsMicrosoft)
                events.Add(Event(before, ChangeType.Removed, nowUtc, Describe(before)));
        }
        return (events, next);
    }

    private static Dictionary<string, ServiceRecord> Enumerate(ServiceBaseline? baseline)
    {
        var display = new Dictionary<string, (string Display, bool IsDriver)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sc in ServiceController.GetServices())
            using (sc) display[sc.ServiceName] = (sc.DisplayName, false);
        foreach (var sc in ServiceController.GetDevices())
            using (sc) display[sc.ServiceName] = (sc.DisplayName, true);

        var records = new Dictionary<string, ServiceRecord>(StringComparer.OrdinalIgnoreCase);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")
            ?? throw new InvalidOperationException("Services key not readable");

        foreach (var (name, (displayName, isDriver)) in display)
        {
            // Some services (security products especially) lock their key against standard users.
            // Track them by name anyway — their config just stays empty — so one locked key
            // doesn't fail the whole source.
            RegistryKey? key;
            try { key = root.OpenSubKey(name); }
            catch (Exception) { key = null; }
            using var _ = key;
            var type = key?.GetValue("Type") is int t ? t : 0;
            if ((type & UserServiceInstanceFlag) != 0 || IsUserServiceInstance(name, root)) continue;

            var imagePath = key?.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var serviceDll = ReadServiceDll(key);
            var record = new ServiceRecord
            {
                Name = name,
                DisplayName = displayName,
                ImagePath = imagePath,
                ServiceDll = serviceDll,
                Start = key?.GetValue("Start") is int s ? s : -1,
                Type = type,
                Account = key?.GetValue("ObjectName") as string ?? "",
                IsDriver = isDriver
            };

            // Reuse the cached verdict when the binary hasn't changed.
            var cached = baseline?.Services.GetValueOrDefault(name);
            var sameBinary = cached != null &&
                             cached.ImagePath.Equals(imagePath, StringComparison.OrdinalIgnoreCase) &&
                             cached.ServiceDll.Equals(serviceDll, StringComparison.OrdinalIgnoreCase);
            records[name] = record with { IsMicrosoft = sameBinary ? cached!.IsMicrosoft : IsMicrosoftBinary(record) };
        }
        return records;
    }

    private static bool IsUserServiceInstance(string name, RegistryKey servicesRoot)
    {
        var match = InstanceSuffix().Match(name);
        if (!match.Success) return false;
        var template = match.Groups["template"].Value;
        RegistryKey? key;
        try { key = servicesRoot.OpenSubKey(template); } catch (Exception) { return false; }
        using var _ = key;
        return key?.GetValue("Type") is int t && (t & UserServiceTemplateFlag) != 0;
    }

    private static string ReadServiceDll(RegistryKey? key)
    {
        if (key == null) return "";
        RegistryKey? parameters;
        try { parameters = key.OpenSubKey("Parameters"); } catch (Exception) { parameters = null; }
        using var _ = parameters;
        var fromParameters = parameters?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!string.IsNullOrEmpty(fromParameters)) return fromParameters;
        return key.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    private static bool IsMicrosoftBinary(ServiceRecord svc)
    {
        var path = ResolveBinary(svc.ServiceDll.Length > 0 ? svc.ServiceDll : svc.ImagePath);
        if (path.Length == 0 || !File.Exists(path)) return false; // unknown → treat as third-party and surface it
        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
            return company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ImagePath → file path: expands env vars, \SystemRoot\, \??\, relative System32 paths and arguments.</summary>
    public static string ResolveBinary(string imagePath)
    {
        var s = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (s.Length == 0) return "";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(windows, s[12..]);
        else if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
        else if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) ||
                 s.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(windows, s);

        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : s.Trim('"');
        }
        foreach (var ext in new[] { ".exe", ".sys", ".dll" })
        {
            var idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return s[..(idx + ext.Length)];
        }
        return s;
    }

    private static List<string> Changes(ServiceRecord before, ServiceRecord after)
    {
        var changes = new List<string>();
        var binaryChanged = !before.ImagePath.Equals(after.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                            !before.ServiceDll.Equals(after.ServiceDll, StringComparison.OrdinalIgnoreCase);

        if (after.IsMicrosoft && before.IsMicrosoft)
            return changes; // Windows servicing its own components

        if (binaryChanged)
        {
            if (!before.ImagePath.Equals(after.ImagePath, StringComparison.OrdinalIgnoreCase))
                changes.Add($"ImagePath: \"{before.ImagePath}\" → \"{after.ImagePath}\"");
            if (!before.ServiceDll.Equals(after.ServiceDll, StringComparison.OrdinalIgnoreCase))
                changes.Add($"ServiceDll: \"{before.ServiceDll}\" → \"{after.ServiceDll}\"");
            if (before.IsMicrosoft && !after.IsMicrosoft)
                changes.Add("binary is no longer a Microsoft file");
        }
        if (!before.Account.Equals(after.Account, StringComparison.OrdinalIgnoreCase))
            changes.Add($"Account: {Or(before.Account)} → {Or(after.Account)}");
        if (before.Start != after.Start)
            changes.Add($"Start: {StartName(before.Start)} → {StartName(after.Start)}");
        return changes;
    }

    private static ChangeEvent Event(ServiceRecord svc, ChangeType type, DateTime nowUtc, string details, ServiceRecord? before = null)
    {
        var label = svc.IsDriver ? "Driver" : "Windows Service";
        var binary = ResolveBinary(svc.ServiceDll.Length > 0 ? svc.ServiceDll : svc.ImagePath);
        string? folder = null;
        try { folder = binary.Length > 0 && Path.IsPathFullyQualified(binary) ? Path.GetDirectoryName(binary) : null; } catch { }

        InstalledApp ToApp(ServiceRecord r) => new()
        {
            KeyPath = $@"{(r.IsDriver ? "DRIVER" : "SERVICE")}\{r.Name}",
            Scope = r.IsDriver ? "DRIVERS" : "SERVICES",
            Name = $"[{label}] {r.DisplayName}",
            InstallLocation = folder ?? "",
            InstallSource = Describe(r),
            InstallType = label,
            InstalledFor = "All users",
            RawValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ServiceName"] = r.Name,
                ["DisplayName"] = r.DisplayName,
                ["ImagePath"] = r.ImagePath,
                ["ServiceDll"] = r.ServiceDll,
                ["Start"] = StartName(r.Start),
                ["ObjectName"] = r.Account,
                ["Type"] = $"0x{r.Type:X}"
            }
        };

        return new ChangeEvent
        {
            App = ToApp(svc),
            PreviousApp = before == null ? null : ToApp(before),
            ChangeType = type,
            DetectedAt = nowUtc,
            Source = svc.IsDriver ? DetectionSource.Driver : DetectionSource.Service,
            Details = details
        };
    }

    private static string Describe(ServiceRecord svc) =>
        $"Service: {svc.Name} | Start: {StartName(svc.Start)} | Account: {Or(svc.Account)} | Path: {Or(svc.ServiceDll.Length > 0 ? svc.ServiceDll : svc.ImagePath)}";

    private static string StartName(int start) => start switch
    {
        0 => "Boot",
        1 => "System",
        2 => "Automatic",
        3 => "Manual",
        4 => "Disabled",
        _ => "Unknown"
    };

    private static string Or(string value) => string.IsNullOrEmpty(value) ? "(default)" : value;
}
