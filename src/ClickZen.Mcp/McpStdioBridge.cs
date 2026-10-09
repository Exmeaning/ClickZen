using System.Diagnostics;
using System.Text.Json;
using ClickZen.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClickZen.Mcp;

/// <summary>Where and how the stdio bridge reaches the running ClickZen.</summary>
public sealed record McpBridgeOptions
{
    public required Uri Endpoint { get; init; }

    public string Token { get; init; } = "";

    /// <summary>Started (without arguments) when nothing listens on <see cref="Endpoint"/>; null = never.</summary>
    public string? LaunchExecutable { get; init; }

    /// <summary>How long to wait for a launched instance to start serving.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Why the HTTP server cannot be reached even if launched (shown to the AI), e.g. "MCP is disabled".</summary>
    public string? UnavailableReason { get; init; }
}

/// <summary>
/// <c>ClickZen.exe --mcp-stdio</c>: an MCP server on stdin/stdout that forwards tool calls to the MCP server
/// of the running ClickZen (Streamable HTTP). For clients that only launch stdio servers (Claude Desktop,
/// Codex…). The token is read from the local settings, so client configs contain no secret.
/// When ClickZen is not reachable, <c>tools/list</c> still answers from the built-in tool list and calls
/// explain how to fix it; the bridge reconnects on the next call.
/// </summary>
public sealed class McpStdioBridge : IAsyncDisposable
{
    private readonly McpBridgeOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly IReadOnlyList<Tool> _offlineTools;
    private McpClient? _client;
    private bool _launched;

    public McpStdioBridge(McpBridgeOptions options, ILoggerFactory? loggers = null)
    {
        _options = options;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _log = _loggers.CreateLogger<McpStdioBridge>();
        _offlineTools = new ClickZenTools(null).CreateTools().Select(t => t.ProtocolTool).ToList();
    }

    /// <summary>Serves stdin/stdout until the client closes stdin or <paramref name="ct"/> fires.</summary>
    public Task RunStdioAsync(CancellationToken ct) =>
        RunAsync(new StdioServerTransport(McpServerHost.ServerName, _loggers), ct);

    /// <summary>Serves an arbitrary transport (tests use stream pairs).</summary>
    public async Task RunAsync(ITransport transport, CancellationToken ct)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = McpServerHost.ServerName, Title = "ClickZen (stdio bridge)", Version = AppInfo.Version },
            ServerInstructions = ClickZenTools.Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = ListToolsAsync,
                CallToolHandler = CallToolAsync,
            },
        };

        await using var server = McpServer.Create(transport, options, _loggers);
        await server.RunAsync(ct);
    }

    private async ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken ct)
    {
        var client = await TryConnectAsync(launch: false, ct);
        if (client is null)
        {
            return new ListToolsResult { Tools = _offlineTools.ToList() };
        }

        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return new ListToolsResult { Tools = tools.Select(t => t.ProtocolTool).ToList() };
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException or IOException)
        {
            _log.LogWarning(ex, "tools/list forwarding failed");
            await ResetAsync();
            return new ListToolsResult { Tools = _offlineTools.ToList() };
        }
    }

    private async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var p = request.Params ?? throw new McpProtocolException("Missing params.", McpErrorCode.InvalidParams);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var client = await TryConnectAsync(launch: true, ct);
            if (client is null)
            {
                return Error(UnreachableMessage());
            }

            try
            {
                var args = p.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                return await client.CallToolAsync(p.Name, args, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt == 0)
            {
                _log.LogWarning(ex, "Forwarding {Tool} failed; reconnecting", p.Name);
                await ResetAsync();
            }
            catch (McpProtocolException ex)
            {
                return Error(ex.Message);
            }
            catch (McpException ex)
            {
                return Error(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                await ResetAsync();
                return Error(UnreachableMessage() + " (" + ex.Message + ")");
            }
        }

        return Error(UnreachableMessage());
    }

    private string UnreachableMessage() =>
        _options.UnavailableReason
        ?? $"ClickZen is not reachable at {_options.Endpoint}. Start ClickZen and make sure the MCP server is enabled on its AI page.";

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private async Task<McpClient?> TryConnectAsync(bool launch, CancellationToken ct)
    {
        if (_client is { } existing)
        {
            return existing;
        }

        if (_options.UnavailableReason is not null)
        {
            return null;
        }

        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var client = await ConnectOnceAsync(ct);
            if (client is null && launch && !_launched && _options.LaunchExecutable is { } exe && File.Exists(exe))
            {
                _launched = true;
                _log.LogInformation("Launching {Exe}", exe);
                try
                {
                    using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _log.LogWarning(ex, "Could not launch ClickZen");
                }

                var deadline = Stopwatch.StartNew();
                while (client is null && deadline.Elapsed < _options.LaunchTimeout)
                {
                    await Task.Delay(750, ct);
                    client = await ConnectOnceAsync(ct);
                }
            }

            _client = client;
            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<McpClient?> ConnectOnceAsync(CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_options.Token.Length > 0)
        {
            headers["Authorization"] = "Bearer " + _options.Token;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = _options.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            Name = "clickzen-bridge",
        }, _loggers);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            return await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "clickzen-stdio-bridge", Version = AppInfo.Version },
            }, _loggers, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException or IOException or OperationCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Could not connect to {Endpoint}", _options.Endpoint);
            return null;
        }
    }

    private async Task ResetAsync()
    {
        var c = _client;
        _client = null;
        if (c is not null)
        {
            try
            {
                await c.DisposeAsync();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Disposing the forwarding client failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _connectLock.Dispose();
    }
}
