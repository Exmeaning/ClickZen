using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClickZen.Core.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Core.Variables;

public sealed record VariableSyncOptions
{
    public int Port { get; init; } = 9527;

    /// <summary>Address to listen on (default: all interfaces).</summary>
    public IPAddress Address { get; init; } = IPAddress.Any;

    /// <summary>Shared secret clients send in <c>hello</c>. Empty = no authentication.</summary>
    public string Token { get; init; } = "";

    public int MaxClients { get; init; } = 16;

    /// <summary>A client that sends nothing for this long is disconnected (clients should ping).</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The first message must arrive within this time.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Longest accepted line (bytes); longer messages close the connection.</summary>
    public int MaxMessageBytes { get; init; } = 64 * 1024;
}

/// <summary>A connected client as shown in the UI.</summary>
public sealed record VariableSyncClientInfo(int Id, string Name, EndPoint? RemoteEndPoint, DateTimeOffset ConnectedAt, IReadOnlyCollection<string> Subscriptions);

/// <summary>
/// Line-delimited JSON over TCP (protocol 2, see docs/variable-sync-protocol.md). Exposes a
/// <see cref="VariableStore"/> to other programs: clients read and write variables and get pushed
/// changes they subscribed to. Per-variable <see cref="SyncDirection"/> decides what is visible
/// (Send/Both) and what is writable (Receive/Both); variables with <see cref="SyncDirection.None"/>
/// are invisible. Pushes are event driven – no polling.
/// </summary>
public sealed class VariableSyncServer : IAsyncDisposable
{
    public const int ProtocolVersion = 2;

    private readonly VariableStore _store;
    private readonly Func<string, SyncDirection> _direction;
    private readonly VariableSyncOptions _options;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _nextId;

    /// <param name="direction">Sync direction of a variable name (None hides it).</param>
    public VariableSyncServer(VariableStore store, Func<string, SyncDirection> direction, VariableSyncOptions options, ILogger? log = null)
    {
        _store = store;
        _direction = direction;
        _options = options;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Port actually bound (useful with port 0 in tests).</summary>
    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public event EventHandler? ClientsChanged;

    public IReadOnlyList<VariableSyncClientInfo> Clients =>
        _clients.Values.Where(c => c.Authenticated && !c.Closing.IsCancellationRequested).OrderBy(c => c.Id).Select(c => c.Info).ToArray();

    /// <summary>Generates a random URL-safe token.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(_options.Address, _options.Port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _store.Changed += OnStoreChanged;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
        _log.LogInformation("Variable sync server listening on {Address}:{Port}", _options.Address, Port);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Changed -= OnStoreChanged;
        await _cts.CancelAsync();
        _listener?.Stop();
        _listener = null;
        foreach (var c in _clients.Values)
        {
            c.Close();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var c in _clients.Values)
        {
            await c.Completion;
        }

        _cts.Dispose();
    }

    // ------------------------------------------------------------------ accept / connection

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.LogWarning(ex, "Accept failed");
                continue;
            }

            socket.NoDelay = true;
            if (_clients.Count >= _options.MaxClients)
            {
                _ = RejectAsync(socket, "too_many_clients", "Too many clients.");
                continue;
            }

            var client = new Client(Interlocked.Increment(ref _nextId), socket);
            _clients[client.Id] = client;
            client.Completion = Task.Run(() => RunClientAsync(client, ct), CancellationToken.None);
        }
    }

    private static async Task RejectAsync(Socket socket, string code, string message)
    {
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            await stream.WriteAsync(Encode(w => WriteError(w, null, code, message)));
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private async Task RunClientAsync(Client client, CancellationToken serverCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt, client.Closing.Token);
        var ct = linked.Token;
        var stream = new NetworkStream(client.Socket, ownsSocket: true);
        client.Stream = stream;
        var reader = PipeReader.Create(stream);
        try
        {
            var authenticated = false;
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(authenticated ? _options.IdleTimeout : _options.HandshakeTimeout);
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await client.SendAsync(Encode(w => WriteError(w, null, "timeout", authenticated ? "No message received in time." : "Handshake timed out.")));
                    break;
                }

                var buffer = result.Buffer;
                while (TryReadLine(ref buffer, out var line))
                {
                    if (line.Length > _options.MaxMessageBytes)
                    {
                        await client.SendAsync(Encode(w => WriteError(w, null, "too_large", "Message too large.")));
                        return;
                    }

                    if (line.IsEmpty || IsWhitespace(line))
                    {
                        continue;
                    }

                    var keep = authenticated
                        ? await HandleAsync(client, line)
                        : authenticated = await HandshakeAsync(client, line);
                    if (!keep)
                    {
                        return;
                    }
                }

                if (buffer.Length > _options.MaxMessageBytes)
                {
                    await client.SendAsync(Encode(w => WriteError(w, null, "too_large", "Message too large.")));
                    return;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Connection dropped or server stopping.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync client {Id} failed", client.Id);
        }
        finally
        {
            await reader.CompleteAsync();
            client.Close();
            if (_clients.TryRemove(client.Id, out _) && client.Authenticated)
            {
                _log.LogInformation("Sync client {Name} ({Endpoint}) disconnected", client.Name, client.RemoteEndPoint);
                ClientsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static bool IsWhitespace(ReadOnlySequence<byte> line)
    {
        foreach (var seg in line)
        {
            foreach (var b in seg.Span)
            {
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var pos = buffer.PositionOf((byte)'\n');
        if (pos is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, pos.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));
        return true;
    }

    // ------------------------------------------------------------------ messages

    private async Task<bool> HandshakeAsync(Client client, ReadOnlySequence<byte> line)
    {
        if (!TryParse(line, out var doc))
        {
            await client.SendAsync(Encode(w => WriteError(w, null, "bad_json", "Invalid JSON.")));
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var id = GetId(root);
            if (GetString(root, "type") != "hello")
            {
                await client.SendAsync(Encode(w => WriteError(w, id, "hello_required", "The first message must be hello.")));
                return false;
            }

            if (root.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var proto) && proto != ProtocolVersion)
            {
                await client.SendAsync(Encode(w => WriteError(w, id, "protocol", $"Unsupported protocol {proto}; this server speaks {ProtocolVersion}.")));
                return false;
            }

            var token = GetString(root, "token") ?? "";
            if (_options.Token.Length > 0 && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_options.Token)))
            {
                _log.LogWarning("Sync client from {Endpoint} failed authentication", client.RemoteEndPoint);
                await client.SendAsync(Encode(w => WriteError(w, id, "auth", "Invalid token.")));
                return false;
            }

            client.Name = GetString(root, "client") is { Length: > 0 } n ? n[..Math.Min(64, n.Length)] : "client";
            client.Authenticated = true;
            await client.SendAsync(Encode(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "welcome");
                WriteIdProperty(w, id);
                w.WriteNumber("protocol", ProtocolVersion);
                w.WriteString("server", "ClickZen " + AppInfo.Version);
                w.WriteEndObject();
            }));
            _log.LogInformation("Sync client {Name} connected from {Endpoint}", client.Name, client.RemoteEndPoint);
            ClientsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    private async Task<bool> HandleAsync(Client client, ReadOnlySequence<byte> line)
    {
        if (!TryParse(line, out var doc))
        {
            await client.SendAsync(Encode(w => WriteError(w, null, "bad_json", "Invalid JSON.")));
            return true;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var id = GetId(root);
            var type = GetString(root, "type");
            switch (type)
            {
                case "ping":
                    await client.SendAsync(Encode(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "pong");
                        WriteIdProperty(w, id);
                        w.WriteEndObject();
                    }));
                    break;

                case "get":
                {
                    var name = GetString(root, "name");
                    if (string.IsNullOrEmpty(name) || !CanRead(name) || !_store.TryGet(name, out var value))
                    {
                        await client.SendAsync(Encode(w => WriteError(w, id, "not_found", $"Variable '{name}' is not shared.")));
                        break;
                    }

                    await client.SendAsync(Encode(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "value");
                        WriteIdProperty(w, id);
                        w.WriteString("name", name);
                        w.WritePropertyName("value");
                        value.WriteJson(w);
                        w.WriteEndObject();
                    }));
                    break;
                }

                case "getAll":
                {
                    var values = Readable();
                    await client.SendAsync(Encode(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "values");
                        WriteIdProperty(w, id);
                        w.WriteStartObject("values");
                        foreach (var (n, v) in values)
                        {
                            w.WritePropertyName(n);
                            v.WriteJson(w);
                        }

                        w.WriteEndObject();
                        w.WriteEndObject();
                    }));
                    break;
                }

                case "set":
                {
                    var name = GetString(root, "name");
                    if (string.IsNullOrEmpty(name) || !root.TryGetProperty("value", out var v))
                    {
                        await client.SendAsync(Encode(w => WriteError(w, id, "bad_request", "set needs name and value.")));
                        break;
                    }

                    if (!CanWrite(name))
                    {
                        await client.SendAsync(Encode(w => WriteError(w, id, "read_only", $"Variable '{name}' does not accept writes.")));
                        break;
                    }

                    _store.Set(name, VariableValue.FromJson(v), VariableChangeSource.Network, client.Origin);
                    await client.SendAsync(Encode(w => WriteOk(w, id)));
                    break;
                }

                case "subscribe":
                case "unsubscribe":
                {
                    var names = GetNames(root);
                    lock (client.Subscriptions)
                    {
                        foreach (var n in names)
                        {
                            if (type == "subscribe")
                            {
                                client.Subscriptions.Add(n);
                            }
                            else
                            {
                                client.Subscriptions.Remove(n);
                            }
                        }
                    }

                    await client.SendAsync(Encode(w => WriteOk(w, id)));
                    ClientsChanged?.Invoke(this, EventArgs.Empty);

                    // Send current values so a subscriber starts in sync.
                    if (type == "subscribe")
                    {
                        foreach (var n in names.Where(n => n != "*" && CanRead(n)))
                        {
                            if (_store.TryGet(n, out var cur))
                            {
                                await client.SendAsync(EncodeChanged(n, cur, VariableChangeSource.Reset));
                            }
                        }
                    }

                    break;
                }

                default:
                    await client.SendAsync(Encode(w => WriteError(w, id, "unknown_type", $"Unknown message type '{type}'.")));
                    break;
            }
        }

        return true;
    }

    private void OnStoreChanged(object? sender, VariableChange change)
    {
        if (!CanRead(change.Name))
        {
            return;
        }

        byte[]? payload = null;
        foreach (var c in _clients.Values)
        {
            if (!c.Authenticated || c.Origin == change.Origin)
            {
                continue; // Do not echo a client's own write back to it.
            }

            bool wanted;
            lock (c.Subscriptions)
            {
                wanted = c.Subscriptions.Contains("*") || c.Subscriptions.Contains(change.Name);
            }

            if (wanted)
            {
                payload ??= EncodeChanged(change.Name, change.NewValue, change.Source);
                _ = c.SendAsync(payload);
            }
        }
    }

    private bool CanRead(string name) => _direction(name) is SyncDirection.Send or SyncDirection.Both;

    private bool CanWrite(string name) => _direction(name) is SyncDirection.Receive or SyncDirection.Both;

    private List<(string Name, VariableValue Value)> Readable()
    {
        var all = new SortedDictionary<string, VariableValue>(StringComparer.Ordinal);
        for (var s = _store; s is not null; s = s.Parent)
        {
            foreach (var (n, v) in s.Snapshot())
            {
                all.TryAdd(n, v);
            }
        }

        return all.Where(kv => CanRead(kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // ------------------------------------------------------------------ JSON helpers

    private static bool TryParse(ReadOnlySequence<byte> line, out JsonDocument doc)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                doc = null!;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Request id echoed in the reply (string or number), so clients can correlate.</summary>
    private static JsonElement? GetId(JsonElement e) =>
        e.TryGetProperty("id", out var p) && p.ValueKind is JsonValueKind.String or JsonValueKind.Number ? p.Clone() : null;

    private static List<string> GetNames(JsonElement e)
    {
        var names = new List<string>();
        if (e.TryGetProperty("names", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            names.AddRange(arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0));
        }

        if (GetString(e, "name") is { Length: > 0 } single)
        {
            names.Add(single);
        }

        return names;
    }

    private static void WriteIdProperty(Utf8JsonWriter w, JsonElement? id)
    {
        if (id is { } v)
        {
            w.WritePropertyName("id");
            v.WriteTo(w);
        }
    }

    private static void WriteOk(Utf8JsonWriter w, JsonElement? id)
    {
        w.WriteStartObject();
        w.WriteString("type", "ok");
        WriteIdProperty(w, id);
        w.WriteEndObject();
    }

    private static void WriteError(Utf8JsonWriter w, JsonElement? id, string code, string message)
    {
        w.WriteStartObject();
        w.WriteString("type", "error");
        WriteIdProperty(w, id);
        w.WriteString("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    }

    private static byte[] EncodeChanged(string name, VariableValue value, VariableChangeSource source) => Encode(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", "changed");
        w.WriteString("name", name);
        w.WritePropertyName("value");
        value.WriteJson(w);
        w.WriteString("source", source switch
        {
            VariableChangeSource.Network => "network",
            VariableChangeSource.User => "user",
            VariableChangeSource.Reset => "reset",
            _ => "engine",
        });
        w.WriteEndObject();
    });

    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            write(w);
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    // ------------------------------------------------------------------ client

    private sealed class Client(int id, Socket socket)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public int Id { get; } = id;
        public Socket Socket { get; } = socket;
        public EndPoint? RemoteEndPoint { get; } = SafeEndpoint(socket);
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
        public string Origin { get; } = "sync:" + id;
        public string Name { get; set; } = "";
        public bool Authenticated { get; set; }
        public HashSet<string> Subscriptions { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource Closing { get; } = new();
        public NetworkStream? Stream { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;

        public VariableSyncClientInfo Info
        {
            get
            {
                lock (Subscriptions)
                {
                    return new VariableSyncClientInfo(Id, Name, RemoteEndPoint, ConnectedAt, Subscriptions.ToArray());
                }
            }
        }

        public async Task SendAsync(byte[] payload)
        {
            var stream = Stream;
            if (stream is null || Closing.IsCancellationRequested)
            {
                return;
            }

            await _writeLock.WaitAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await stream.WriteAsync(payload, timeout.Token);
            }
            catch (Exception)
            {
                Close(); // Slow or dead client.
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Close()
        {
            try
            {
                Closing.Cancel();
                Socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // Already closed.
            }
        }

        private static EndPoint? SafeEndpoint(Socket s)
        {
            try
            {
                return s.RemoteEndPoint;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
