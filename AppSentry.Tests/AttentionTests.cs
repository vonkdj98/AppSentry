using AppSentry.Core.Detection;
using AppSentry.Models;

namespace AppSentry.Tests;

public class AttentionTests
{
    private static ChangeEvent Service(ChangeType type, Dictionary<string, string> after, Dictionary<string, string>? before = null,
        DetectionSource source = DetectionSource.Service, string details = "", string name = "[Windows Service] Contoso Agent") => new()
    {
        App = new InstalledApp { Name = name, RawValues = after },
        PreviousApp = before == null ? null : new InstalledApp { Name = name, RawValues = before },
        ChangeType = type,
        Source = source,
        Details = details
    };

    [Fact]
    public void Service_binary_swapped_to_non_microsoft_is_critical()
    {
        var ev = Service(ChangeType.Modified,
            new() { ["ImagePath"] = @"C:\Users\Public\x.exe" }, new() { ["ImagePath"] = @"C:\Windows\System32\svchost.exe" },
            details: "ImagePath: … → …; binary is no longer a Microsoft file");
        Assert.Equal(AttentionLevel.Critical, AttentionClassifier.Classify(ev).Level);
    }

    [Fact]
    public void Changed_service_binary_and_new_driver_are_warnings()
    {
        Assert.Equal(AttentionLevel.Warning, AttentionClassifier.Classify(Service(ChangeType.Modified,
            new() { ["ImagePath"] = @"C:\B\svc.exe" }, new() { ["ImagePath"] = @"C:\A\svc.exe" })).Level);
        Assert.Equal("New driver", AttentionClassifier.Classify(Service(ChangeType.Installed, [], source: DetectionSource.Driver)).Reason);
    }

    [Fact]
    public void Security_tool_removal_is_critical()
    {
        var ev = new ChangeEvent
        {
            App = new InstalledApp { Name = "CrowdStrike Windows Sensor", Publisher = "CrowdStrike, Inc." },
            ChangeType = ChangeType.Removed,
            Source = DetectionSource.Registry
        };
        Assert.Equal(AttentionLevel.Critical, AttentionClassifier.Classify(ev).Level);
    }

    [Fact]
    public void Elevated_task_is_a_warning_and_routine_update_is_nothing()
    {
        var task = new ChangeEvent
        {
            App = new InstalledApp { Name = @"[Scheduled Task] \Updater", RawValues = new() { ["RunAs"] = "SYSTEM" } },
            ChangeType = ChangeType.Installed,
            Source = DetectionSource.ScheduledTask
        };
        var update = new ChangeEvent
        {
            App = new InstalledApp { Name = "7-Zip", Version = "24.08" },
            PreviousVersion = "23.01",
            ChangeType = ChangeType.Updated,
            Source = DetectionSource.Registry
        };
        Assert.Equal(AttentionLevel.Warning, AttentionClassifier.Classify(task).Level);
        Assert.Equal(AttentionLevel.None, AttentionClassifier.Classify(update).Level);
    }
}
