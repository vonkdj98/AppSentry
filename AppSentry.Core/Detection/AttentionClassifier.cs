using AppSentry.Models;

namespace AppSentry.Core.Detection;

public enum AttentionLevel
{
    None,
    Notice,   // worth knowing, not alarming (new service, portable program)
    Warning,  // look at it (failed install, new driver, changed uninstaller, SYSTEM task)
    Critical  // likely tampering (service binary swapped to non-Microsoft, security tool removed)
}

public sealed record Attention(AttentionLevel Level, string Reason)
{
    public static readonly Attention None = new(AttentionLevel.None, "");
}

/// <summary>
/// Decides which changes deserve a human's eyes. Shared by the UI ("Needs a look"), the
/// notification rules and the Windows event log level, so they always agree.
/// Rules compare the before/after values the sources capture, not display strings.
/// </summary>
public static class AttentionClassifier
{
    private static readonly string[] SecurityProducts =
    [
        "crowdstrike", "falcon sensor", "windows defender", "microsoft defender", "sentinelone", "sentinel agent",
        "cybereason", "sophos", "carbon black", "bitdefender", "eset", "malwarebytes", "symantec", "mcafee",
        "trend micro", "wazuh", "huntress", "sysmon", "tanium", "cortex xdr", "trellix", "webroot"
    ];

    public static Attention Classify(ChangeEvent ev)
    {
        var app = ev.App;
        var security = IsSecurityProduct(app);

        if (ev.ChangeType == ChangeType.Failed)
            return new(AttentionLevel.Warning, "Install or removal failed");

        if (ev.ChangeType == ChangeType.Removed && security)
            return new(AttentionLevel.Critical, "Security software removed");

        switch (ev.Source)
        {
            case DetectionSource.Service or DetectionSource.Driver:
                return ClassifyService(ev, security);
            case DetectionSource.ScheduledTask:
                return ClassifyTask(ev);
            case DetectionSource.FileSystem when ev.ChangeType == ChangeType.Installed:
                return new(AttentionLevel.Notice, "Program added without an installer");
        }

        if (ev.ChangeType == ChangeType.Modified && ev.PreviousApp is { } before &&
            (Changed(before.UninstallString, app.UninstallString) || Changed(before.QuietUninstallString, app.QuietUninstallString)))
            return new(AttentionLevel.Warning, "Uninstall command changed");

        return Attention.None;
    }

    private static Attention ClassifyService(ChangeEvent ev, bool security)
    {
        var isDriver = ev.Source == DetectionSource.Driver;
        var after = ev.App.RawValues ?? [];

        switch (ev.ChangeType)
        {
            case ChangeType.Installed when isDriver:
                return new(AttentionLevel.Warning, "New driver");
            case ChangeType.Installed:
                var account = after.GetValueOrDefault("ObjectName") ?? "";
                return account.Length == 0 || account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
                    ? new(AttentionLevel.Notice, "New service running as SYSTEM")
                    : new(AttentionLevel.Notice, "New service");
            case ChangeType.Removed when security:
                return new(AttentionLevel.Critical, "Security service removed");
            case ChangeType.Modified when ev.PreviousApp?.RawValues is { } before:
                if (ev.Details.Contains("no longer a Microsoft file", StringComparison.OrdinalIgnoreCase))
                    return new(AttentionLevel.Critical, "Service binary replaced with a non-Microsoft file");
                if (Changed(before.GetValueOrDefault("ImagePath"), after.GetValueOrDefault("ImagePath")) ||
                    Changed(before.GetValueOrDefault("ServiceDll"), after.GetValueOrDefault("ServiceDll")))
                    return new(AttentionLevel.Warning, isDriver ? "Driver file changed" : "Service binary changed");
                if (Changed(before.GetValueOrDefault("ObjectName"), after.GetValueOrDefault("ObjectName")))
                    return new(AttentionLevel.Warning, "Service account changed");
                if (security && Changed(before.GetValueOrDefault("Start"), after.GetValueOrDefault("Start")))
                    return new(AttentionLevel.Warning, "Security service start type changed");
                return new(AttentionLevel.Notice, "Start type changed");
        }
        return Attention.None;
    }

    private static Attention ClassifyTask(ChangeEvent ev)
    {
        var after = ev.App.RawValues ?? [];
        var runAs = after.GetValueOrDefault("RunAs") ?? "";
        var elevated = runAs.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) || runAs.Contains("S-1-5-18") ||
                       runAs.Contains("highest privileges", StringComparison.OrdinalIgnoreCase);

        return ev.ChangeType switch
        {
            ChangeType.Installed when elevated => new(AttentionLevel.Warning, "New task runs with elevated rights"),
            ChangeType.Installed => new(AttentionLevel.Notice, "New scheduled task"),
            ChangeType.Modified => new(AttentionLevel.Warning, "Task action or account changed"),
            _ => Attention.None
        };
    }

    private static bool IsSecurityProduct(InstalledApp app)
    {
        var text = $"{app.Name} {app.Publisher}".ToLowerInvariant();
        return SecurityProducts.Any(text.Contains);
    }

    private static bool Changed(string? before, string? after) =>
        !string.Equals((before ?? "").Trim(), (after ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}
