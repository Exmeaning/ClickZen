using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClickZen.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClickZen.Mcp;

/// <summary>Options of the embedded MCP HTTP server.</summary>
public sealed record McpServerHostOptions
{
    /// <summary>TCP port (0 = any free port, used by tests).</summary>
    public int Port { get; init; } = Core.Settings.McpSettings.DefaultPort;

    /// <summary>Listen on all interfaces instead of loopback only.</summary>
    public bool AllowRemote { get; init; }

    /// <summary>Bearer token clients must send. Empty disables authentication (tests only – the app always sets one).</summary>
    public string Token { get; init; } = "";

    /// <summary>Route of the Streamable HTTP endpoint.</summary>
    public string Path { get; init; } = McpServerHost.EndpointPath;
}

/// <summary>One tool invocation, for the AI page's activity list.</summary>
/// <param name="Arguments">Short single-line summary of the arguments (long strings and images elided).</param>
public sealed record McpCallRecord(DateTimeOffset At, string Client, string Tool, string Arguments, bool Success, string? Error, long ElapsedMs);

/// <summary>
/// The MCP server: an in-process Kestrel host serving the Streamable HTTP transport (stateless) at
/// <c>/mcp</c>. Adds bearer-token authentication, Origin / Host checks (DNS rebinding protection),
/// per-capability filtering of <c>tools/list</c> and <c>tools/call</c>, and a call log.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    public const string EndpointPath = "/mcp";
    public const string ServerName = "clickzen";

    private readonly IClickZenBackend _backend;
    private readonly Func<McpCapability, bool> _allowed;
    private readonly McpServerHostOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly byte[] _tokenBytes;
    private WebApplication? _app;

    /// <param name="allowed">Evaluated on every request, so capability switches apply immediately.</param>
    public McpServerHost(IClickZenBackend backend, Func<McpCapability, bool> allowed, McpServerHostOptions options, ILoggerFactory? loggers = null)
    {
        _backend = backend;
        _allowed = allowed;
        _options = options;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _log = _loggers.CreateLogger<McpServerHost>();
        _tokenBytes = Encoding.UTF8.GetBytes(options.Token ?? "");
    }

    /// <summary>Raised (on a worker thread) after every tool call.</summary>
    public event EventHandler<McpCallRecord>? ToolCalled;

    /// <summary>Port actually bound.</summary>
    public int Port { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>Endpoint URL for local clients, e.g. http://127.0.0.1:9528/mcp.</summary>
    public string LocalUrl => $"http://127.0.0.1:{Port}{_options.Path}";

    /// <summary>Generates a random URL-safe bearer token (32 bytes of entropy).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Binds and starts serving. Throws <see cref="IOException"/> when the port is taken.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpServerHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggers);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
            if (_options.AllowRemote)
            {
                k.ListenAnyIP(_options.Port);
            }
            else
            {
                k.Listen(IPAddress.Loopback, _options.Port);
            }
        });

        var tools = new ClickZenTools(_backend, _allowed);
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = ServerName, Title = "ClickZen", Version = AppInfo.Version };
                o.ServerInstructions = ClickZenTools.Instructions;
            })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(tools.CreateTools().Select(t => (McpServerTool)new GuardedTool(t, this)))
            .WithRequestFilters(f => f.AddListToolsFilter(next => async (ctx, token) =>
            {
                var result = await next(ctx, token);
                result.Tools = result.Tools.Where(t => _allowed(ClickZenTools.CapabilityOf(t.Name))).ToList();
                return result;
            }));

        var app = builder.Build();
        app.Use(GuardAsync);
        app.MapMcp(_options.Path);
        app.MapGet("/", () => Results.Text($"ClickZen MCP server. Endpoint: {_options.Path} (Streamable HTTP, Authorization: Bearer <token>)."));

        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        Port = addresses?.Select(a => new Uri(a.Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal)).Port).FirstOrDefault() ?? _options.Port;
        _app = app;
        _log.LogInformation("MCP server listening on {Scope}:{Port}{Path}", _options.AllowRemote ? "all interfaces" : "127.0.0.1", Port, _options.Path);
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Forced shutdown of lingering requests.
        }
        finally
        {
            await app.DisposeAsync();
        }

        _log.LogInformation("MCP server stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ------------------------------------------------------------------ HTTP guard

    /// <summary>Origin / Host checks (DNS rebinding) and bearer-token authentication.</summary>
    private async Task GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!_options.AllowRemote && !IsLoopbackHost(ctx.Request.Host.Host))
        {
            await Reject(ctx, StatusCodes.Status403Forbidden, "Host not allowed.");
            return;
        }

        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsAllowedOrigin(origin, ctx.Request.Host.Host))
        {
            await Reject(ctx, StatusCodes.Status403Forbidden, "Origin not allowed.");
            return;
        }

        if (ctx.Request.Path == "/" && HttpMethods.IsGet(ctx.Request.Method))
        {
            await next(ctx);
            return;
        }

        if (_tokenBytes.Length > 0 && !HasValidToken(ctx.Request))
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"ClickZen\"";
            await Reject(ctx, StatusCodes.Status401Unauthorized, "Missing or invalid token. Send the token shown on ClickZen's AI page as 'Authorization: Bearer <token>'.");
            return;
        }

        await next(ctx);
    }

    private bool HasValidToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        string? presented = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = header["Bearer ".Length..].Trim();
        }
        else if (request.Headers.TryGetValue("X-ClickZen-Token", out var alt))
        {
            presented = alt.ToString().Trim();
        }

        return presented is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _tokenBytes);
    }

    internal static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>Browsers send Origin; only pages served from the same host (or loopback) may call.</summary>
    internal bool IsAllowedOrigin(string origin, string requestHost)
    {
        if (origin == "null" || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (IsLoopbackHost(uri.Host))
        {
            return true;
        }

        return _options.AllowRemote && uri.Host.Equals(requestHost, StringComparison.OrdinalIgnoreCase);
    }

    private static Task Reject(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code = -32001, message }, id = (string?)null });
        return ctx.Response.WriteAsync(body);
    }

    // ------------------------------------------------------------------ tool guard

    private void OnToolCalled(McpCallRecord record)
    {
        try
        {
            ToolCalled?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ToolCalled handler failed");
        }
    }

    /// <summary>Single-line argument summary for the activity list.</summary>
    internal static string SummarizeArguments(IDictionary<string, JsonElement>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "";
        }

        var parts = new List<string>(args.Count);
        foreach (var (key, value) in args)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { } s
                    ? key.Contains("base64", StringComparison.OrdinalIgnoreCase) ? $"<{s.Length} chars>" : "\"" + UiHierarchy.Truncate(s.Replace('\n', ' '), 60) + "\""
                    : "null",
                JsonValueKind.Array or JsonValueKind.Object => UiHierarchy.Truncate(value.GetRawText(), 60),
                _ => value.GetRawText(),
            };
            parts.Add($"{key}={text}");
        }

        return UiHierarchy.Truncate(string.Join(", ", parts), 200);
    }

    /// <summary>Wraps each tool: refuses disabled capabilities and reports every call.</summary>
    private sealed class GuardedTool(McpServerTool inner, McpServerHost host) : DelegatingMcpServerTool(inner)
    {
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var name = ProtocolTool.Name;
            var client = request.Server?.ClientInfo is { } ci ? $"{ci.Name} {ci.Version}".Trim() : "unknown";
            var args = SummarizeArguments(request.Params?.Arguments);
            var sw = Stopwatch.StartNew();
            if (!host._allowed(ClickZenTools.CapabilityOf(name)))
            {
                var msg = $"The tool '{name}' is disabled in ClickZen (AI page → {ClickZenTools.CapabilityOf(name)} tools).";
                host.OnToolCalled(new McpCallRecord(DateTimeOffset.Now, client, name, args, false, msg, 0));
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = msg }] };
            }

            try
            {
                var result = await base.InvokeAsync(request, cancellationToken);
                string? error = result.IsError == true ? FirstText(result) : null;
                host.OnToolCalled(new McpCallRecord(DateTimeOffset.Now, client, name, args, result.IsError != true, error, sw.ElapsedMilliseconds));
                return result;
            }
            catch (McpException ex)
            {
                host.OnToolCalled(new McpCallRecord(DateTimeOffset.Now, client, name, args, false, ex.Message, sw.ElapsedMilliseconds));
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
            }
            catch (OperationCanceledException)
            {
                host.OnToolCalled(new McpCallRecord(DateTimeOffset.Now, client, name, args, false, "cancelled", sw.ElapsedMilliseconds));
                throw;
            }
            catch (Exception ex)
            {
                host._log.LogWarning(ex, "MCP tool {Tool} failed", name);
                var msg = $"{ex.GetType().Name}: {ex.Message}";
                host.OnToolCalled(new McpCallRecord(DateTimeOffset.Now, client, name, args, false, msg, sw.ElapsedMilliseconds));
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = msg }] };
            }
        }

        private static string? FirstText(CallToolResult r) => r.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
    }
}
