using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AppSentry.Core.Ipc;
using AppSentry.Core.Util;
using AppSentry.Models;
using Microsoft.Win32.SafeHandles;

namespace AppSentry.Core.Backend;

/// <summary>
/// Talks to the AppSentry Windows service over \\.\pipe\AppSentry. Reconnects on its own if
/// the service restarts. Refuses a pipe that isn't served from session 0 (services), so another
/// user can't impersonate the service by creating the pipe name first.
/// </summary>
public sealed class PipeBackend : IMonitorBackend
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly string _pipeName;
    private readonly bool _requireServiceSession;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<PipeMessage>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private int _nextId;
    private volatile bool _disposed;
    private EngineStatus _status = new() { Mode = "Service" };

    private PipeBackend(string pipeName, bool requireServiceSession)
    {
        _pipeName = pipeName;
        _requireServiceSession = requireServiceSession;
    }

    /// <summary>Connects to a running service, or returns null if none answers within <paramref name="timeout"/>.</summary>
    public static PipeBackend? TryConnect(TimeSpan timeout, string pipeName = PipeProtocol.DefaultPipeName, bool requireServiceSession = true)
    {
        var backend = new PipeBackend(pipeName, requireServiceSession);
        try
        {
            if (backend.Connect(timeout)) return backend;
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Service pipe not usable: {ex.Message}");
        }
        backend.Dispose();
        return null;
    }

    public string Mode => "Service";

    public bool CanModify { get; private set; }

    public string ClientName { get; private set; } = "";

    public EngineStatus Status => _status;

    public event EventHandler<IReadOnlyList<ChangeEvent>>? EventsDetected;

    public event EventHandler<EngineStatus>? StatusChanged;

    public async Task StartAsync()
    {
        var hello = await RequestAsync<HelloResponse>(PipeProtocol.Ops.Hello).ConfigureAwait(false);
        if (hello == null) throw new InvalidOperationException("The AppSentry service did not answer.");
        if (hello.ProtocolVersion != PipeProtocol.Version)
            throw new InvalidOperationException($"The installed AppSentry service speaks protocol v{hello.ProtocolVersion}; this app needs v{PipeProtocol.Version}. Reinstall the service from this build.");
        CanModify = hello.CanModify;
        ClientName = hello.ClientName;
        Editions = hello.Editions ?? [];
        SetStatus(hello.Status);
    }

    public async Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query) =>
        await RequestAsync<HistoryPage>(PipeProtocol.Ops.HistoryPage, query).ConfigureAwait(false) ?? new HistoryPage();

    public Task<ChangeEvent?> GetEventAsync(long id) => RequestAsync<ChangeEvent>(PipeProtocol.Ops.Event, id);

    public async Task<List<InstalledApp>> GetInventoryAsync() =>
        await RequestAsync<List<InstalledApp>>(PipeProtocol.Ops.Inventory).ConfigureAwait(false) ?? [];

    public async Task<Sources.PersistenceInventory> GetPersistenceAsync() =>
        await RequestAsync<Sources.PersistenceInventory>(PipeProtocol.Ops.Persistence).ConfigureAwait(false) ?? new Sources.PersistenceInventory();

    public Task RequestScanAsync() => RequestAsync<object>(PipeProtocol.Ops.Scan);

    public Task ClearHistoryAsync() => RequestAsync<object>(PipeProtocol.Ops.ClearHistory);

    public async Task<List<ExclusionEntry>> GetExclusionsAsync() =>
        await RequestAsync<List<ExclusionEntry>>(PipeProtocol.Ops.GetExclusions).ConfigureAwait(false) ?? [];

    public Task SaveExclusionsAsync(List<ExclusionEntry> entries) => RequestAsync<object>(PipeProtocol.Ops.SetExclusions, entries);

    public async Task<EngineSettings> GetSettingsAsync() =>
        await RequestAsync<EngineSettings>(PipeProtocol.Ops.GetSettings).ConfigureAwait(false) ?? new EngineSettings();

    public Task SaveSettingsAsync(EngineSettings settings) => RequestAsync<object>(PipeProtocol.Ops.SetSettings, settings);

    public IReadOnlyList<string> Editions { get; private set; } = [];

    public Task<JsonElement?> CallEditionAsync(string op, object? data = null) => RequestAsync<JsonElement?>(op, data);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _pipe?.Dispose(); } catch { }
        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private bool Connect(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            return false;
        }

        if (_requireServiceSession &&
            (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var session) || session != 0))
        {
            pipe.Dispose();
            EngineLog.Warn($"Refused \\\\.\\pipe\\{_pipeName}: it is not served by a Windows service");
            return false;
        }

        _pipe = pipe;
        _ = Task.Run(() => ReadLoopAsync(pipe));
        return true;
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null) break;
                PipeMessage? message;
                try { message = AppJson.Deserialize<PipeMessage>(line); }
                catch (JsonException) { continue; }
                if (message == null) continue;

                if (message.Push != null)
                    HandlePush(message);
                else if (message.Id is { } id && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(message);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { /* fall through to reconnect */ }

        foreach (var (id, tcs) in _pending)
        {
            if (_pending.TryRemove(id, out _)) tcs.TrySetException(new IOException("Lost connection to the AppSentry service."));
        }
        if (_disposed) return;

        SetStatus(_status with { IsScanning = false, LastError = "Lost connection to the AppSentry service — reconnecting…" });
        _ = Task.Run(ReconnectLoopAsync);
    }

    private async Task ReconnectLoopAsync()
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                if (Connect(TimeSpan.FromSeconds(2)))
                {
                    await StartAsync().ConfigureAwait(false);
                    SetStatus(_status with { LastError = null, Notice = "Reconnected to the AppSentry service" });
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* keep trying */ }
        }
    }

    private void HandlePush(PipeMessage message)
    {
        try
        {
            switch (message.Push)
            {
                case PipeProtocol.Pushes.Events when message.Data is { } data:
                    var events = data.Deserialize<List<ChangeEvent>>(AppJson.Options) ?? [];
                    EventsDetected?.Invoke(this, events);
                    break;
                case PipeProtocol.Pushes.Status when message.Data is { } data:
                    if (data.Deserialize<EngineStatus>(AppJson.Options) is { } status) SetStatus(status);
                    break;
            }
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Bad push from service: {ex.Message}");
        }
    }

    private void SetStatus(EngineStatus status)
    {
        _status = status with { Mode = "Service" };
        StatusChanged?.Invoke(this, _status);
    }

    // ── Requests ──────────────────────────────────────────────────────────────

    private async Task<T?> RequestAsync<T>(string op, object? arg = null)
    {
        var pipe = _pipe ?? throw new IOException("Not connected to the AppSentry service.");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new PipeMessage
        {
            Id = id,
            Op = op,
            Data = arg == null ? null : JsonSerializer.SerializeToElement(arg, AppJson.Options)
        };
        var bytes = Encoding.UTF8.GetBytes(AppJson.Serialize(request) + "\n");

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"The AppSentry service did not answer '{op}'.");
        }

        var response = await tcs.Task.ConfigureAwait(false);
        if (response.Ok != true) throw new InvalidOperationException(response.Error ?? $"'{op}' failed");
        return response.Data is { } data && typeof(T) != typeof(object) ? data.Deserialize<T>(AppJson.Options) : default;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);
}
