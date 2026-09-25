using AppSentry.Core.Backend;
using AppSentry.Core.Engine;
using AppSentry.Core.Ipc;
using AppSentry.Core.Service;
using AppSentry.Core.Util;
using AppSentry.Models;
using Microsoft.Win32;
using Xunit.Abstractions;

namespace AppSentry.Tests;

/// <summary>Engine + PipeServer in this process, driven through the same PipeBackend the tray app uses.</summary>
[Trait("Category", "Integration")]
[Collection("Machine")]
public class PipeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Tray_client_reads_history_changes_settings_and_receives_pushes()
    {
        using var dir = new TempDir();
        var pipeName = "AppSentryTest-" + Guid.NewGuid().ToString("N");
        using var engine = new MonitorEngine(new EngineOptions { DataDir = dir.Path });
        var baseline = new ManualResetEventSlim();
        engine.StatusChanged += (_, s) => { if (!s.IsScanning && s.LastScanUtc != null) baseline.Set(); };
        engine.Start();
        using var server = new PipeServer(engine, pipeName);
        server.Start();
        Assert.True(baseline.Wait(TimeSpan.FromMinutes(3)));

        // A real service runs in session 0; a pipe served from a user session must be refused.
        Assert.Null(PipeBackend.TryConnect(TimeSpan.FromSeconds(2), pipeName, requireServiceSession: true));

        using var client = PipeBackend.TryConnect(TimeSpan.FromSeconds(2), pipeName, requireServiceSession: false);
        Assert.NotNull(client);
        await client.StartAsync();
        output.WriteLine($"Connected as {client.ClientName}, CanModify={client.CanModify}");
        Assert.True(client.Status.TrackedApps > 10);
        Assert.True((await client.GetInventoryAsync()).Count > 10);

        if (client.CanModify)
        {
            await client.SaveExclusionsAsync([new ExclusionEntry("Pipe Test App*", true, false)]);
            Assert.Equal("Pipe Test App*", Assert.Single(await client.GetExclusionsAsync()).AppName);
            await client.SaveSettingsAsync(new EngineSettings { ScanIntervalMinutes = 10, RealtimeEnabled = true });
            Assert.Equal(10, (await client.GetSettingsAsync()).ScanIntervalMinutes);
        }

        var pushed = new TaskCompletionSource<ChangeEvent>();
        client.EventsDetected += (_, events) =>
        {
            var mine = events.FirstOrDefault(e => e.App.Name == "Pipe Test App");
            if (mine != null) pushed.TrySetResult(mine);
        };

        var path = $@"{RegistryPaths.Uninstall}\AppSentry.PipeTest";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("DisplayName", "Pipe Test App");
                key.SetValue("DisplayVersion", "1.0");
            }
            await client.RequestScanAsync();
            var ev = await pushed.Task.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(ChangeType.Installed, ev.ChangeType);
            if (client.CanModify) Assert.True(ev.Silent); // the exclusion set over the pipe applied

            var history = (await client.GetHistoryPageAsync(new HistoryQuery { Limit = 500 })).Events;
            Assert.Contains(history, e => e.Id == ev.Id);
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false); } catch { }
        }
    }

    [Fact]
    public void Event_log_message_is_key_value_lines()
    {
        var text = WindowsEventLogWriter.Format(new ChangeEvent
        {
            App = new InstalledApp { Name = "7-Zip", Version = "24.08", Publisher = "Igor Pavlov", KeyPath = @"HKLM\X\7-Zip" },
            ChangeType = ChangeType.Updated,
            PreviousVersion = "23.01",
            ChangedBy = @"CONTOSO\alex",
            DetectedAt = new DateTime(2026, 9, 25, 15, 0, 0, DateTimeKind.Utc)
        });

        Assert.StartsWith("AppSentry: Updated 7-Zip 24.08", text);
        Assert.Contains("PreviousVersion: 23.01", text);
        Assert.Contains(@"ChangedBy: CONTOSO\alex", text);
        Assert.Equal(1001, WindowsEventLogWriter.EventIdFor(ChangeType.Updated));
    }
}
