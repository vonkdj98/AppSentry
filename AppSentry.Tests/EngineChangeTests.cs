using AppSentry.Core.Engine;
using AppSentry.Core.Util;
using AppSentry.Models;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace AppSentry.Tests;

/// <summary>
/// Makes real, harmless, self-cleaning changes as the current user and checks the engine
/// reports each one exactly once: a fake per-user uninstall entry under HKCU, and a
/// scheduled task in the user's own scheduler.
/// </summary>
[Trait("Category", "Integration")]
public class EngineChangeTests(ITestOutputHelper output)
{
    private const string KeyName = "AppSentry.SmokeTest";
    private const string TaskName = "AppSentrySmokeTest";

    [Fact]
    public void Registry_and_task_changes_are_reported_once_each()
    {
        using var dir = new TempDir();
        using var engine = new MonitorEngine(new EngineOptions { DataDir = dir.Path });
        var scanned = new AutoResetEvent(false);
        engine.StatusChanged += (_, s) => { if (!s.IsScanning && s.LastScanUtc != null) scanned.Set(); };

        void Scan()
        {
            engine.RequestScan("test", TimeSpan.Zero);
            Assert.True(scanned.WaitOne(TimeSpan.FromMinutes(3)), "scan did not finish");
        }

        List<ChangeEvent> Mine() => engine.GetHistory()
            .Where(e => e.App.Name.Contains("AppSentry Smoke Test") || e.App.Name.Contains(TaskName))
            .OrderBy(e => e.Id).ToList();

        var uninstallPath = $@"{RegistryPaths.Uninstall}\{KeyName}";
        try
        {
            engine.Start();
            Assert.True(scanned.WaitOne(TimeSpan.FromMinutes(3)), "baseline scan did not finish");

            using (var key = Registry.CurrentUser.CreateSubKey(uninstallPath))
            {
                key.SetValue("DisplayName", "AppSentry Smoke Test");
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("Publisher", "AppSentry Tests");
                key.SetValue("UninstallString", @"C:\SmokeTest\uninstall.exe");
                key.SetValue("EstimatedSize", 2048, RegistryValueKind.DWord);
            }
            Scan();
            using (var key = Registry.CurrentUser.OpenSubKey(uninstallPath, writable: true)!) key.SetValue("DisplayVersion", "1.1");
            Scan();
            using (var key = Registry.CurrentUser.OpenSubKey(uninstallPath, writable: true)!) key.SetValue("UninstallString", @"C:\Elsewhere\evil.exe");
            Scan();
            Registry.CurrentUser.DeleteSubKey(uninstallPath);
            Scan();

            var created = ProcessRunner.Run("schtasks.exe", $"/Create /TN {TaskName} /TR \"cmd.exe /c exit\" /SC ONCE /ST 23:59 /F", TimeSpan.FromSeconds(30));
            Assert.True(created.Succeeded, created.StdErr);
            Scan();
            ProcessRunner.Run("schtasks.exe", $"/Delete /TN {TaskName} /F", TimeSpan.FromSeconds(30));
            Scan();

            var mine = Mine();
            foreach (var ev in mine) output.WriteLine($"{ev.ChangeType,-9} {ev.App.Name} v{ev.App.Version} [{ev.Source}] {ev.Details}");

            Assert.Collection(mine,
                e => { Assert.Equal(ChangeType.Installed, e.ChangeType); Assert.Equal(2048 * 1024L, e.SizeBytes ?? e.App.EstimatedSizeKb * 1024); Assert.NotNull(e.OccurredAt); },
                e => { Assert.Equal(ChangeType.Updated, e.ChangeType); Assert.Equal("1.0", e.PreviousVersion); Assert.Equal("1.0", e.PreviousApp!.RawValues!["DisplayVersion"]); },
                e => { Assert.Equal(ChangeType.Modified, e.ChangeType); Assert.Contains("UninstallString", e.Details); },
                e => Assert.Equal(ChangeType.Removed, e.ChangeType),
                e => { Assert.Equal(ChangeType.Installed, e.ChangeType); Assert.Equal(DetectionSource.ScheduledTask, e.Source); Assert.Contains("cmd.exe", e.App.InstallSource); },
                e => { Assert.Equal(ChangeType.Removed, e.ChangeType); Assert.Equal(DetectionSource.ScheduledTask, e.Source); });
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKey(uninstallPath, throwOnMissingSubKey: false); } catch { }
            ProcessRunner.Run("schtasks.exe", $"/Delete /TN {TaskName} /F", TimeSpan.FromSeconds(30));
        }
    }
}
