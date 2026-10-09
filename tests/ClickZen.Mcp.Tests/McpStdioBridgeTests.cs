using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClickZen.Mcp.Tests;

/// <summary>The stdio bridge, driven through in-memory pipes instead of the process's console.</summary>
public sealed class McpStdioBridgeTests
{
    private sealed class BridgeHarness : IAsyncDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public BridgeHarness(McpStdioBridge bridge)
        {
            Bridge = bridge;
            var transport = new StreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream(), "bridge-test");
            _run = bridge.RunAsync(transport, _cts.Token);
        }

        public McpStdioBridge Bridge { get; }

        public Task<McpClient> ConnectAsync() =>
            McpClient.CreateAsync(new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()),
                cancellationToken: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }

            await Bridge.DisposeAsync();
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Forwards_list_and_calls_to_the_running_server()
    {
        var backend = new FakeBackend();
        await using var host = new McpServerHost(backend, _ => true, new McpServerHostOptions { Port = 0, Token = "bridge-token" });
        await host.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = new BridgeHarness(new McpStdioBridge(new McpBridgeOptions { Endpoint = new Uri(host.LocalUrl), Token = "bridge-token" }));
        await using var client = await harness.ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var r = await client.CallToolAsync("tap", new Dictionary<string, object?> { ["x"] = 10, ["y"] = 20 }, cancellationToken: TestContext.Current.CancellationToken);
        var shot = await client.CallToolAsync("take_screenshot", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, t => t.Name == "adb_shell");
        Assert.NotEqual(true, r.IsError);
        Assert.Contains("tap 10,20 0", backend.Calls);
        Assert.Single(shot.Content.OfType<ImageContentBlock>());
        Assert.Contains("refWidth", client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_lists_builtin_tools_and_explains_calls()
    {
        await using var harness = new BridgeHarness(new McpStdioBridge(new McpBridgeOptions
        {
            Endpoint = new Uri("http://127.0.0.1:1/mcp"),
            UnavailableReason = "The MCP server is disabled in ClickZen.",
        }));
        await using var client = await harness.ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var r = await client.CallToolAsync("list_devices", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ClickZenTools.Capabilities.Count, tools.Count);
        Assert.True(r.IsError);
        Assert.Contains("disabled in ClickZen", string.Concat(r.Content.OfType<TextContentBlock>().Select(t => t.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_server_reports_how_to_fix_it()
    {
        await using var harness = new BridgeHarness(new McpStdioBridge(new McpBridgeOptions { Endpoint = new Uri("http://127.0.0.1:1/mcp") }));
        await using var client = await harness.ConnectAsync();

        var r = await client.CallToolAsync("get_status", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(r.IsError);
        Assert.Contains("AI page", string.Concat(r.Content.OfType<TextContentBlock>().Select(t => t.Text)), StringComparison.Ordinal);
    }
}
