using ClickZen.App.Shell;
using ClickZen.Core.Settings;
using ClickZen.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ClickZen.App.Services;

/// <summary>
/// <c>--selftest-mcp</c>: verifies AI access without devices or adb. Starts the MCP server of this instance on a
/// free port with a fresh token, connects with the official SDK client over HTTP (list, status, devices,
/// 401 without token, capability switch), then launches <c>ClickZen.exe --mcp-stdio</c> and repeats the basic
/// calls through the stdio bridge. Exits 0 on success. Use a fresh CLICKZEN_DATA_DIR.
/// </summary>
internal sealed class McpSelfTest
{
    private readonly MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public McpSelfTest(MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exitCode = 0;
        var mcp = _services.GetRequiredService<McpService>();
        try
        {
            await Task.Delay(500);
            var settings = _services.GetRequiredService<SettingsService>();
            settings.Update(s =>
            {
                s.Mcp.Port = FreePort();
                s.Mcp.AllowRemote = false;
            });
            await mcp.InitializeAsync(_window.DispatcherQueue);
            await mcp.SetEnabledAsync(true);
            Check(mcp.IsRunning, $"server did not start: {mcp.LastError}");
            var token = mcp.Token;
            Check(token.Length >= 32, "token was not generated");
            _log.LogInformation("MCP selftest: server at {Url}", mcp.LocalUrl);

            // --- HTTP -------------------------------------------------------------------------
            await using (var client = await ConnectHttpAsync(mcp.LocalUrl, token))
            {
                var tools = await client.ListToolsAsync();
                Check(tools.Count == ClickZenTools.Capabilities.Count, $"expected {ClickZenTools.Capabilities.Count} tools, got {tools.Count}");
                var status = await client.CallToolAsync("get_status");
                Check(status.IsError != true && Text(status).Contains("\"version\"", StringComparison.Ordinal), "get_status failed: " + Text(status));
                var devices = await client.CallToolAsync("list_devices");
                Check(devices.IsError != true, "list_devices failed: " + Text(devices));
                var scheme = await client.CallToolAsync("get_scheme");
                Check(scheme.IsError != true, "get_scheme failed: " + Text(scheme));
                var set = await client.CallToolAsync("set_variable", new Dictionary<string, object?> { ["name"] = "mcp_selftest", ["value"] = 42 });
                Check(set.IsError != true, "set_variable failed: " + Text(set));
                var vars = await client.CallToolAsync("get_variables");
                Check(Text(vars).Contains("\"mcp_selftest\":42", StringComparison.Ordinal), "variable not stored: " + Text(vars));

                mcp.SetAllowed(McpCapability.Shell, false);
                var hidden = await client.ListToolsAsync();
                Check(hidden.All(t => t.Name != "adb_shell"), "disabled tool still listed");
                var refused = await client.CallToolAsync("adb_shell", new Dictionary<string, object?> { ["command"] = "id" });
                Check(refused.IsError == true, "disabled tool was executed");
                mcp.SetAllowed(McpCapability.Shell, true);
            }

            using (var http = new HttpClient())
            {
                var res = await http.PostAsync(mcp.LocalUrl, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Check(res.StatusCode == System.Net.HttpStatusCode.Unauthorized, $"request without token returned {(int)res.StatusCode}");
            }

            await Task.Delay(200);
            Check(mcp.CallCount >= 6 && mcp.Calls.Count == mcp.CallCount, $"call log incomplete ({mcp.Calls.Count}/{mcp.CallCount})");

            // --- stdio bridge (separate process, same data dir) ------------------------------------
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("process path unknown");
            var stdio = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "clickzen-selftest",
                Command = exe,
                Arguments = [McpClientConfig.StdioArgument],
                WorkingDirectory = Path.GetDirectoryName(exe),
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["CLICKZEN_DATA_DIR"] = Environment.GetEnvironmentVariable("CLICKZEN_DATA_DIR"),
                    ["CLICKZEN_MCP_NO_LAUNCH"] = "1",
                },
                StandardErrorLines = line => _log.LogInformation("MCP selftest bridge: {Line}", line),
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using (var bridge = await McpClient.CreateAsync(stdio, cancellationToken: timeout.Token))
            {
                var tools = await bridge.ListToolsAsync(cancellationToken: timeout.Token);
                Check(tools.Count == ClickZenTools.Capabilities.Count, $"bridge listed {tools.Count} tools");
                var status = await bridge.CallToolAsync("get_status", cancellationToken: timeout.Token);
                Check(status.IsError != true && Text(status).Contains("\"version\"", StringComparison.Ordinal), "bridge get_status failed: " + Text(status));
            }

            _log.LogInformation("MCP selftest passed ({Calls} call(s) logged)", mcp.CallCount);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "MCP selftest failed");
            exitCode = 2;
        }
        finally
        {
            await mcp.StopAsync();
        }

        var marker = Environment.GetEnvironmentVariable("CLICKZEN_SMOKE_MARKER");
        if (!string.IsNullOrEmpty(marker))
        {
            await File.WriteAllTextAsync(marker, exitCode == 0 ? "ok" : "fail");
        }

        Environment.Exit(exitCode);
    }

    private static async Task<McpClient> ConnectHttpAsync(string url, string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }

    private static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
