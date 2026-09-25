using AppSentry.Core.Engine;
using AppSentry.Models;
using Xunit.Abstractions;

namespace AppSentry.Tests;

/// <summary>
/// Runs the real engine against this machine (read-only: registry, Store API, event log,
/// services). Filter with: dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class EngineSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public void First_scan_baselines_and_a_rescan_reports_no_phantom_changes()
    {
        using var dir = new TempDir();
        using var engine = new MonitorEngine(new EngineOptions { DataDir = dir.Path });

        var scanned = new AutoResetEvent(false);
        EngineStatus? last = null;
        engine.StatusChanged += (_, s) =>
        {
            if (s.IsScanning || s.LastScanUtc == null) return;
            last = s;
            scanned.Set();
        };

        engine.Start();
        Assert.True(scanned.WaitOne(TimeSpan.FromMinutes(3)), "first scan did not finish");
        output.WriteLine($"Scan 1: {last!.TrackedApps} apps in {last.LastScanSeconds:0.0}s; " +
                         string.Join(", ", last.Sources.Select(kv => $"{kv.Key}={kv.Value}")));

        Assert.True(last.TrackedApps > 10, "expected a real inventory");
        Assert.Equal("ok", last.Sources["Registry"]);
        Assert.Empty(engine.GetHistory()); // the first run is a baseline, never a flood of "Installed"

        engine.RequestScan("test", TimeSpan.Zero);
        Assert.True(scanned.WaitOne(TimeSpan.FromMinutes(3)), "second scan did not finish");
        output.WriteLine($"Scan 2: {last.TrackedApps} apps in {last.LastScanSeconds:0.0}s");

        var history = engine.GetHistory();
        foreach (var ev in history) output.WriteLine($"  {ev.ChangeType} {ev.App.Name} [{ev.Source}] {ev.Details}");
        Assert.True(history.Count <= 2, $"unexpected changes between back-to-back scans: {history.Count}");
    }
}
