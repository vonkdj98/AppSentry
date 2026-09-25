using System.ServiceProcess;
using AppSentry.Core.Engine;
using AppSentry.Core.Ipc;
using AppSentry.Core.Util;

namespace AppSentry.Core.Service;

/// <summary>
/// The AppSentry Windows service (AppSentry.exe --service). Runs the engine as LocalSystem, so it
/// sees every user's hive, every task and the whole event log, keeps monitoring when nobody is
/// signed in, and keeps one machine-wide history. Tray apps connect through <see cref="PipeServer"/>.
/// </summary>
public sealed class AppSentryWindowsService : ServiceBase
{
    public const string Name = "AppSentry";

    private MonitorEngine? _engine;
    private PipeServer? _pipe;

    public AppSentryWindowsService()
    {
        ServiceName = Name;
        CanHandlePowerEvent = true;
        CanShutdown = true;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        var dataDir = ServiceDataDir.Ensure();
        EngineLog.Initialize(dataDir);

        try { WindowsEventLogWriter.EnsureSource(); }
        catch (Exception ex) { EngineLog.Error("Could not register the AppSentry event source", ex); }

        _engine = new MonitorEngine(new EngineOptions { DataDir = dataDir, Mode = EngineMode.Service });
        _engine.EventsDetected += (_, events) => WindowsEventLogWriter.Write(events);
        _engine.Start();

        _pipe = new PipeServer(_engine);
        _pipe.Start();
    }

    protected override void OnStop()
    {
        _pipe?.Dispose();
        _pipe = null;
        _engine?.Dispose(); // waits for an in-flight scan to commit
        _engine = null;
    }

    protected override void OnShutdown() => OnStop();

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic)
            _engine?.RequestScan("resumed from sleep", TimeSpan.FromSeconds(15));
        return true;
    }
}
