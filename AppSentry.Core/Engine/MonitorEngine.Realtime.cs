using AppSentry.Core.Sources;
using AppSentry.Core.Triggers;
using AppSentry.Core.Util;

namespace AppSentry.Core.Engine;

// Real-time triggers: registry/event log/folder change notifications → debounced scan.
public sealed partial class MonitorEngine
{
    /// <summary>Wait this long after the last change signal before scanning (installers write in bursts).</summary>
    private static readonly TimeSpan TriggerDebounce = TimeSpan.FromSeconds(10);

    private ChangeTriggers? _triggers;

    partial void OnStarted()
    {
        if (_settings.RealtimeEnabled) StartTriggers();
    }

    partial void OnStopping() => StopTriggers();

    partial void OnRealtimeSettingChanged()
    {
        if (_settings.RealtimeEnabled) StartTriggers();
        else StopTriggers();
    }

    private void StartTriggers()
    {
        lock (_schedLock)
        {
            if (_triggers != null || _disposed) return;
            try
            {
                _triggers = new ChangeTriggers(label => RequestScan($"change: {label}", TriggerDebounce));
            }
            catch (Exception ex)
            {
                EngineLog.Error("Real-time triggers failed to start; relying on the interval scan", ex);
                return;
            }
        }
        RefreshTriggers();
    }

    private void StopTriggers()
    {
        ChangeTriggers? triggers;
        lock (_schedLock)
        {
            triggers = _triggers;
            _triggers = null;
        }
        triggers?.Dispose();
    }

    /// <summary>Called after every scan: user hives come and go, so the watch set does too.</summary>
    private void RefreshTriggers()
    {
        ChangeTriggers? triggers;
        lock (_schedLock) triggers = _triggers;
        if (triggers == null) return;
        try
        {
            triggers.Update(
                ChangeTriggers.DefaultRegistryWatches(Context),
                FileSystemSource.GetRoots(Context).Select(r => r.Path).ToList());
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Could not refresh change triggers: {ex.Message}");
        }
    }
}
