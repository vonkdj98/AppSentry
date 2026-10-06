using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AppSentry.Core.Engine;
using AppSentry.Core.Util;
using AppSentry.Models;

namespace AppSentry.Core.Ipc;

/// <summary>
/// Serves the engine to tray apps over a named pipe.
///
/// Security:
///  - Any authenticated user may connect and read (history, inventory, status) and ask for a scan.
///  - Clearing history and changing exclusions/settings require an administrator account.
///    An admin whose token is UAC-filtered still counts (Administrators appears as a deny-only
///    SID), so admins don't have to run the tray app elevated.
///  - Standard users get ReadWrite on the pipe but not CreateNewInstance, so they can't add
///    their own server instances to it.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly MonitorEngine _engine;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Client> _clients = [];
    private readonly object _lock = new();
    private Task? _acceptLoop;
    private bool _created;
    private DateTime _lastUserScanUtc;

    // Any user can connect, so one must not be able to starve the others or the service.
    internal const int MaxLineChars = 8 * 1024 * 1024;  // a rules file to import is the largest request
    internal const int MaxClients = 64;
    internal const int MaxClientsPerUser = 8;
    private static readonly TimeSpan UserScanEvery = TimeSpan.FromMinutes(1);

    public PipeServer(MonitorEngine engine, string pipeName = PipeProtocol.DefaultPipeName)
    {
        _engine = engine;
        _pipeName = pipeName;
    }

    public void Start()
    {
        _engine.EventsDetected += (_, events) => Broadcast(PipeProtocol.Pushes.Events, events);
        _engine.StatusChanged += (_, status) => Broadcast(PipeProtocol.Pushes.Status, status);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _cts.Dispose();
    }

    // ── Accept / serve ────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // The first instance must be ours: if another process created the name first (while the service was
                // restarting), clients would talk to it. Later instances join our own.
                pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | (_created ? PipeOptions.None : PipeOptions.FirstPipeInstance), 0, 0, BuildSecurity());
                _created = true;
                await pipe.WaitForConnectionAsync(_cts.Token);

                var (isAdmin, name) = IdentifyClient(pipe);
                lock (_lock)
                {
                    if (_clients.Count >= MaxClients || (!isAdmin && _clients.Count(c => c.Name == name) >= MaxClientsPerUser))
                    {
                        EngineLog.Warn($"Pipe client {name} refused: too many connections");
                        pipe.Dispose();
                        pipe = null;
                        continue;
                    }
                }
                var client = new Client(pipe, isAdmin, name);
                pipe = null; // owned by the client now
                lock (_lock) _clients.Add(client);
                _ = Task.Run(() => ServeAsync(client));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                EngineLog.Error("Pipe accept failed", ex);
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ServeAsync(Client client)
    {
        EngineLog.Info($"Tray client connected: {client.Name}{(client.IsAdmin ? " (admin)" : " (read-only)")}");
        try
        {
            using var reader = new StreamReader(client.Pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
            var lines = new LineReader(reader, MaxLineChars);
            while (!_cts.IsCancellationRequested)
            {
                var line = await lines.ReadAsync(_cts.Token);
                if (line == null) break;
                PipeMessage? request;
                try { request = AppJson.Deserialize<PipeMessage>(line); }
                catch (JsonException) { continue; }
                if (request?.Id is not { } id || string.IsNullOrEmpty(request.Op)) continue;

                var response = await Task.Run(() => Handle(request, client));
                await client.SendAsync(response with { Id = id });
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidDataException ex)
        {
            EngineLog.Warn($"Pipe client {client.Name} dropped: {ex.Message}");
        }
        catch (IOException) { } // client went away
        catch (Exception ex)
        {
            EngineLog.Error($"Pipe client {client.Name} failed", ex);
        }
        finally
        {
            lock (_lock) _clients.Remove(client);
            client.Dispose();
            EngineLog.Info($"Tray client disconnected: {client.Name}");
        }
    }

    private PipeMessage Handle(PipeMessage request, Client client)
    {
        var op = request.Op!;
        if (PipeProtocol.Ops.Mutating.Contains(op) && !client.IsAdmin)
            return Fail("This requires an administrator account.");

        try
        {
            return op switch
            {
                PipeProtocol.Ops.Hello => Ok(new HelloResponse
                {
                    ProtocolVersion = PipeProtocol.Version,
                    ServiceVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "",
                    CanModify = client.IsAdmin,
                    ClientName = client.Name,
                    Status = _engine.Status,
                    Editions = _engine.Editions.ToList()
                }),
                PipeProtocol.Ops.HistoryPage => Ok(_engine.GetHistoryPage(Arg<HistoryQuery>(request) ?? new HistoryQuery())),
                PipeProtocol.Ops.Event => Ok(_engine.GetEvent(Arg<long>(request))),
                PipeProtocol.Ops.Inventory => Ok(_engine.GetInventory()),
                PipeProtocol.Ops.Persistence => Ok(_engine.GetPersistence()),
                PipeProtocol.Ops.Scan => UserScan(client),
                PipeProtocol.Ops.ClearHistory => Do(_engine.ClearHistory),
                PipeProtocol.Ops.GetExclusions => Ok(_engine.GetExclusions()),
                PipeProtocol.Ops.SetExclusions => Do(() => _engine.SetExclusions(Arg<List<ExclusionEntry>>(request) ?? [])),
                PipeProtocol.Ops.GetSettings => Ok(_engine.Settings),
                PipeProtocol.Ops.SetSettings => Do(() => _engine.UpdateSettings(Arg<EngineSettings>(request) ?? _engine.Settings)),
                _ => Edition(request, client)
            };
        }
        catch (Exception ex)
        {
            EngineLog.Error($"Pipe op {op} failed", ex);
            return Fail(ex.Message);
        }
    }

    /// <summary>Anyone may ask for a scan; a standard user once a minute, so they can't keep the service scanning.</summary>
    private PipeMessage UserScan(Client client)
    {
        if (!client.IsAdmin)
            lock (_lock)
            {
                if (DateTime.UtcNow - _lastUserScanUtc < UserScanEvery) return Fail("A scan was asked for less than a minute ago.");
                _lastUserScanUtc = DateTime.UtcNow;
            }
        _engine.RequestScan($"requested by {client.Name}", TimeSpan.Zero);
        return new PipeMessage { Ok = true };
    }

    /// <summary>Any other op belongs to an edition; it enforces its own admin rules via CallerIsAdmin.</summary>
    private PipeMessage Edition(PipeMessage request, Client client)
    {
        var response = _engine.HandleEditionOp(new Editions.EditionRequest(request.Op!, request.Data, client.IsAdmin, client.Name));
        return new PipeMessage { Ok = response.Ok, Data = response.Data, Error = response.Error };
    }

    private static T? Arg<T>(PipeMessage request) =>
        request.Data is { } data ? data.Deserialize<T>(AppJson.Options) : default;

    private static PipeMessage Ok<T>(T data) =>
        new() { Ok = true, Data = JsonSerializer.SerializeToElement(data, AppJson.Options) };

    private static PipeMessage Do(Action action)
    {
        action();
        return new PipeMessage { Ok = true };
    }

    private static PipeMessage Fail(string error) => new() { Ok = false, Error = error };

    private void Broadcast<T>(string push, T data)
    {
        List<Client> clients;
        lock (_lock) clients = _clients.ToList();
        if (clients.Count == 0) return;
        var message = new PipeMessage { Push = push, Data = JsonSerializer.SerializeToElement(data, AppJson.Options) };
        foreach (var c in clients) _ = c.SendAsync(message);
    }

    // ── Identity / ACL ────────────────────────────────────────────────────────

    private static PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // The creator (this process) needs full control too; when hosted by tests it isn't SYSTEM.
        using var self = WindowsIdentity.GetCurrent();
        if (self.User != null)
            security.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Impersonates the client only on a throwaway thread, with execution-context flow suppressed, so the
    /// client's token can never end up on a pool thread that later writes the log/database or serves ops
    /// (with an Identification-level token those fail with "Access is denied").
    /// </summary>
    private static (bool IsAdmin, string Name) IdentifyClient(NamedPipeServerStream pipe)
    {
        (bool IsAdmin, string Name) result = (false, "unknown");
        var thread = new Thread(() => result = IdentifyOnThisThread(pipe)) { IsBackground = true, Name = "Pipe client check" };
        using (ExecutionContext.SuppressFlow()) thread.Start();
        thread.Join();
        return result;
    }

    private static (bool IsAdmin, string Name) IdentifyOnThisThread(NamedPipeServerStream pipe)
    {
        var isAdmin = false;
        var name = "unknown";
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                name = identity.Name;
                var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
                isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
                          || identity.IsSystem
                          || identity.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == adminSid);
            });
        }
        catch (Exception ex)
        {
            EngineLog.Warn($"Could not identify pipe client: {ex.Message}");
        }
        return (isAdmin, name);
    }

    /// <summary>Lines from the pipe, refusing one longer than the limit (the client is dropped) instead of buffering it all.</summary>
    internal sealed class LineReader(TextReader reader, int maxChars)
    {
        private readonly char[] _buffer = new char[16 * 1024];
        private int _start, _end;

        public async Task<string?> ReadAsync(CancellationToken cancel)
        {
            StringBuilder? line = null;
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await reader.ReadAsync(_buffer.AsMemory(), cancel);
                    if (_end == 0) return line?.ToString(); // end of stream: the last line, if any
                }
                var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
                var stop = newline < 0 ? _end : newline;
                line ??= new StringBuilder();
                if (line.Length + (stop - _start) > maxChars) throw new InvalidDataException($"A request was longer than {maxChars:N0} characters.");
                line.Append(_buffer, _start, stop - _start);
                _start = stop;
                if (newline < 0) continue;
                _start++; // past the newline
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
        }
    }

    private sealed class Client(NamedPipeServerStream pipe, bool isAdmin, string name) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public NamedPipeServerStream Pipe { get; } = pipe;
        public bool IsAdmin { get; } = isAdmin;
        public string Name { get; } = name;

        public async Task SendAsync(PipeMessage message)
        {
            var bytes = Encoding.UTF8.GetBytes(AppJson.Serialize(message) + "\n");
            await _sendLock.WaitAsync();
            try
            {
                if (Pipe.IsConnected) await Pipe.WriteAsync(bytes);
            }
            catch (Exception)
            {
                // Disconnected mid-write; ServeAsync cleans up.
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            try { Pipe.Dispose(); } catch { }
        }
    }
}
