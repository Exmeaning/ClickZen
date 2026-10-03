using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClickZen.Core.Automation;
using ClickZen.Core.Variables;

namespace ClickZen.Core.Tests.Variables;

/// <summary>Protocol tests against a real server on loopback TCP.</summary>
public sealed class VariableSyncServerTests : IAsyncLifetime
{
    private static readonly Dictionary<string, SyncDirection> Directions = new()
    {
        ["score"] = SyncDirection.Both,
        ["status"] = SyncDirection.Send,
        ["command"] = SyncDirection.Receive,
        ["secret"] = SyncDirection.None,
    };

    private readonly VariableStore _store = new();
    private VariableSyncServer _server = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _store.Declare("score", VariableType.Int, 5);
        _store.Set("status", "idle");
        _store.Set("command", "");
        _store.Set("secret", 42);
        _server = Start(new VariableSyncOptions { Token = "t0ken" });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private VariableSyncServer Start(VariableSyncOptions options)
    {
        var server = new VariableSyncServer(_store, n => Directions.GetValueOrDefault(n, SyncDirection.None),
            options with { Port = 0, Address = IPAddress.Loopback });
        server.Start();
        return server;
    }

    [Fact]
    public async Task Hello_with_valid_token_gets_welcome()
    {
        await using var c = await TestClient.ConnectAsync(_server.Port);
        var welcome = await c.RequestAsync("""{"type":"hello","token":"t0ken","client":"test","protocol":2,"id":1}""");
        Assert.Equal("welcome", welcome.Type);
        Assert.Equal(2, welcome.Root.GetProperty("protocol").GetInt32());
        Assert.Equal(1, welcome.Root.GetProperty("id").GetInt32());
        await WaitUntilAsync(() => _server.Clients.Count == 1);
        Assert.Equal("test", _server.Clients[0].Name);
    }

    [Theory]
    [InlineData("""{"type":"hello","token":"wrong","protocol":2}""", "auth")]
    [InlineData("""{"type":"get","name":"score"}""", "hello_required")]
    [InlineData("""{"type":"hello","token":"t0ken","protocol":1}""", "protocol")]
    [InlineData("not json", "bad_json")]
    public async Task Bad_handshake_is_rejected_and_disconnected(string first, string code)
    {
        await using var c = await TestClient.ConnectAsync(_server.Port);
        var reply = await c.RequestAsync(first);
        Assert.Equal("error", reply.Type);
        Assert.Equal(code, reply.Root.GetProperty("code").GetString());
        Assert.True(await c.IsClosedAsync());
        await WaitUntilAsync(() => _server.Clients.Count == 0);
    }

    [Fact]
    public async Task Get_set_and_getAll_respect_directions()
    {
        await using var c = await HelloAsync();

        var score = await c.RequestAsync("""{"type":"get","name":"score","id":"a"}""");
        Assert.Equal("value", score.Type);
        Assert.Equal(5, score.Root.GetProperty("value").GetInt64());
        Assert.Equal("a", score.Root.GetProperty("id").GetString());

        // Receive-only and hidden variables are not readable.
        Assert.Equal("not_found", (await c.RequestAsync("""{"type":"get","name":"command"}""")).Root.GetProperty("code").GetString());
        Assert.Equal("not_found", (await c.RequestAsync("""{"type":"get","name":"secret"}""")).Root.GetProperty("code").GetString());

        var all = await c.RequestAsync("""{"type":"getAll"}""");
        var names = all.Root.GetProperty("values").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["score", "status"], names);

        // Writes: Both and Receive accepted, Send and None rejected.
        Assert.Equal("ok", (await c.RequestAsync("""{"type":"set","name":"score","value":"7"}""")).Type);
        Assert.Equal(VariableValue.FromInt(7), _store.Get("score"));
        Assert.Equal(VariableType.Int, _store.Get("score").Type); // Declared type wins over the string sent.
        Assert.Equal("ok", (await c.RequestAsync("""{"type":"set","name":"command","value":"go"}""")).Type);
        Assert.Equal("go", _store.Get("command").AsString);
        Assert.Equal("read_only", (await c.RequestAsync("""{"type":"set","name":"status","value":"x"}""")).Root.GetProperty("code").GetString());
        Assert.Equal("read_only", (await c.RequestAsync("""{"type":"set","name":"secret","value":1}""")).Root.GetProperty("code").GetString());
        Assert.Equal(42, _store.Get("secret").AsInt);

        Assert.Equal("pong", (await c.RequestAsync("""{"type":"ping"}""")).Type);
        Assert.Equal("unknown_type", (await c.RequestAsync("""{"type":"nope"}""")).Root.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Subscribers_get_initial_value_and_pushed_changes_without_echo()
    {
        await using var a = await HelloAsync("a");
        await using var b = await HelloAsync("b");

        Assert.Equal("ok", (await a.RequestAsync("""{"type":"subscribe","names":["score","secret","status"]}""")).Type);
        var initial = new[] { await a.ReadAsync(), await a.ReadAsync() }.OrderBy(m => m.Root.GetProperty("name").GetString()).ToArray();
        Assert.All(initial, m => Assert.Equal("changed", m.Type));
        Assert.Equal("score", initial[0].Root.GetProperty("name").GetString());
        Assert.Equal("status", initial[1].Root.GetProperty("name").GetString()); // secret is hidden

        Assert.Equal("ok", (await b.RequestAsync("""{"type":"subscribe","name":"*"}""")).Type);

        // Engine change reaches both subscribers.
        _store.Set("status", "battle");
        var pa = await a.ReadAsync();
        Assert.Equal("battle", pa.Root.GetProperty("value").GetString());
        Assert.Equal("engine", pa.Root.GetProperty("source").GetString());
        Assert.Equal("battle", (await b.ReadAsync()).Root.GetProperty("value").GetString());

        // A client's own write is not echoed to it, but others see it.
        Assert.Equal("ok", (await b.RequestAsync("""{"type":"set","name":"score","value":9}""")).Type);
        var pushed = await a.ReadAsync();
        Assert.Equal(9, pushed.Root.GetProperty("value").GetInt64());
        Assert.Equal("network", pushed.Root.GetProperty("source").GetString());
        Assert.Equal("pong", (await b.RequestAsync("""{"type":"ping"}""")).Type); // nothing queued before the pong

        // Hidden variables never push; unchanged values do not push.
        _store.Set("secret", 1);
        _store.Set("score", 9);
        Assert.Equal("ok", (await a.RequestAsync("""{"type":"unsubscribe","name":"status"}""")).Type);
        _store.Set("status", "done");
        Assert.Equal("pong", (await a.RequestAsync("""{"type":"ping"}""")).Type);
        await WaitUntilAsync(() => _server.Clients.Any(c => c.Name == "a" && c.Subscriptions.Count == 2));
    }

    [Fact]
    public async Task Split_and_coalesced_packets_are_framed_by_newline()
    {
        await using var c = await TestClient.ConnectAsync(_server.Port);
        var hello = Encoding.UTF8.GetBytes("""{"type":"hello","token":"t0ken","protocol":2}""" + "\n");
        foreach (var b in hello)
        {
            await c.WriteRawAsync([b]);
        }

        Assert.Equal("welcome", (await c.ReadAsync()).Type);

        await c.WriteRawAsync(Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"id\":1}\r\n\n{\"type\":\"get\",\"name\":\"score\",\"id\":2}\n{\"type\":\"pi"));
        Assert.Equal("pong", (await c.ReadAsync()).Type);
        Assert.Equal(2, (await c.ReadAsync()).Root.GetProperty("id").GetInt32());
        await c.WriteRawAsync(Encoding.UTF8.GetBytes("ng\",\"id\":3}\n"));
        Assert.Equal(3, (await c.ReadAsync()).Root.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Idle_client_is_disconnected()
    {
        await using var server = Start(new VariableSyncOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) });
        await using var c = await TestClient.ConnectAsync(server.Port);
        Assert.Equal("welcome", (await c.RequestAsync("""{"type":"hello"}""")).Type);
        var timeout = await c.ReadAsync();
        Assert.Equal("timeout", timeout.Root.GetProperty("code").GetString());
        Assert.True(await c.IsClosedAsync());
    }

    [Fact]
    public async Task Clients_over_the_limit_are_refused()
    {
        await using var server = Start(new VariableSyncOptions { MaxClients = 1 });
        await using var first = await TestClient.ConnectAsync(server.Port);
        Assert.Equal("welcome", (await first.RequestAsync("""{"type":"hello"}""")).Type);
        await using var second = await TestClient.ConnectAsync(server.Port);
        var refused = await second.ReadAsync();
        Assert.Equal("too_many_clients", refused.Root.GetProperty("code").GetString());
        Assert.True(await second.IsClosedAsync());
    }

    [Fact]
    public async Task Oversized_message_closes_the_connection()
    {
        await using var server = Start(new VariableSyncOptions { MaxMessageBytes = 256 });
        await using var c = await TestClient.ConnectAsync(server.Port);
        Assert.Equal("welcome", (await c.RequestAsync("""{"type":"hello"}""")).Type);
        await c.WriteRawAsync(Encoding.UTF8.GetBytes(new string('x', 1024)));
        Assert.Equal("too_large", (await c.ReadAsync()).Root.GetProperty("code").GetString());
        Assert.True(await c.IsClosedAsync());
    }

    [Fact]
    public void New_tokens_are_random_and_url_safe()
    {
        var a = VariableSyncServer.NewToken();
        Assert.NotEqual(a, VariableSyncServer.NewToken());
        Assert.Matches("^[A-Za-z0-9_-]{24}$", a);
    }

    private async Task<TestClient> HelloAsync(string name = "test")
    {
        var c = await TestClient.ConnectAsync(_server.Port);
        Assert.Equal("welcome", (await c.RequestAsync($$"""{"type":"hello","token":"t0ken","client":"{{name}}","protocol":2}""")).Type);
        return c;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.True(condition());
    }

    private sealed record Message(JsonElement Root)
    {
        public string? Type => Root.GetProperty("type").GetString();
    }

    private sealed class TestClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;

        private TestClient(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
        }

        public static async Task<TestClient> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
            return new TestClient(tcp);
        }

        public Task WriteRawAsync(byte[] data) => _stream.WriteAsync(data, Ct).AsTask();

        public async Task<Message> RequestAsync(string json)
        {
            await WriteRawAsync(Encoding.UTF8.GetBytes(json + "\n"));
            return await ReadAsync();
        }

        public async Task<Message> ReadAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Connection closed.");
            return new Message(JsonDocument.Parse(line).RootElement.Clone());
        }

        public async Task<bool> IsClosedAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                return await _reader.ReadLineAsync(timeout.Token) is null;
            }
            catch (IOException)
            {
                return true;
            }
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
