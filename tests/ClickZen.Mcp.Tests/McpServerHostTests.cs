using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClickZen.Core.Settings;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ClickZen.Mcp.Tests;

/// <summary>End to end: real Kestrel on a free loopback port, the official SDK client over Streamable HTTP.</summary>
public sealed class McpServerHostTests : IAsyncLifetime
{
    private const string Token = "test-token-0123456789";

    private readonly FakeBackend _backend = new();
    private readonly McpSettings _settings = new();
    private readonly List<McpCallRecord> _calls = [];
    private McpServerHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = new McpServerHost(_backend, c => _settings.Allows(c), new McpServerHostOptions { Port = 0, Token = Token });
        _host.ToolCalled += (_, r) =>
        {
            lock (_calls)
            {
                _calls.Add(r);
            }
        };
        await _host.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<McpClient> ConnectAsync(string? token = Token)
    {
        var headers = new Dictionary<string, string>();
        if (token is not null)
        {
            headers["Authorization"] = "Bearer " + token;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_host.LocalUrl),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
        });
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private async Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null) =>
        await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Lists_every_tool_with_annotations_and_server_info()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Equal(ClickZenTools.Capabilities.Keys.ToHashSet(), names);
        foreach (var expected in new[] { "list_devices", "take_screenshot", "tap", "swipe", "press_key", "input_text", "dump_ui", "start_automation", "play_recording", "set_variable", "adb_shell" })
        {
            Assert.Contains(expected, names);
        }

        var screenshot = tools.Single(t => t.Name == "take_screenshot").ProtocolTool;
        Assert.True(screenshot.Annotations?.ReadOnlyHint);
        var shell = tools.Single(t => t.Name == "adb_shell").ProtocolTool;
        Assert.True(shell.Annotations?.DestructiveHint);
        Assert.False(shell.Annotations?.ReadOnlyHint);

        Assert.Equal(McpServerHost.ServerName, client.ServerInfo.Name);
        Assert.Contains("refWidth", client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_or_wrong_token_is_rejected_with_401()
    {
        using var http = new HttpClient();
        var body = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");

        var none = await http.PostAsync(_host.LocalUrl, body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Post, _host.LocalUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong, TestContext.Current.CancellationToken)).StatusCode);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(token: null));
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2025-03-26")]
    public async Task Initialize_handshake_clients_work_over_raw_http(string protocol)
    {
        using var http = new HttpClient();

        async Task<string> PostAsync(string json, bool withVersionHeader)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _host.LocalUrl) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            if (withVersionHeader)
            {
                req.Headers.Add("MCP-Protocol-Version", protocol);
            }

            var res = await http.SendAsync(req, TestContext.Current.CancellationToken);
            var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
            return body;
        }

        var init = await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":""" + "\"" + protocol + "\"" + ""","capabilities":{},"clientInfo":{"name":"legacy","version":"1"}}}""", false);
        Assert.Contains("\"protocolVersion\":\"" + protocol + "\"", init, StringComparison.Ordinal);
        Assert.Contains("clickzen", init, StringComparison.Ordinal);

        var list = await PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", true);
        Assert.Contains("\"take_screenshot\"", list, StringComparison.Ordinal);

        var call = await PostAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"press_key","arguments":{"key":"back"}}}""", true);
        Assert.Contains("key code 4", call, StringComparison.Ordinal);
        Assert.Contains("key 4", _backend.Calls);
    }

    [Fact]
    public async Task Foreign_origin_is_rejected_with_403()
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, _host.LocalUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        req.Headers.Add("Origin", "https://evil.example");

        var res = await http.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Disabled_capability_hides_tools_and_refuses_calls()
    {
        await using var client = await ConnectAsync();
        _settings.AllowShell = false;
        _settings.AllowInput = false;

        var names = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.Name).ToList();
        Assert.DoesNotContain("adb_shell", names);
        Assert.DoesNotContain("tap", names);
        Assert.Contains("take_screenshot", names);

        var r = await CallAsync(client, "adb_shell", new() { ["command"] = "id" });
        Assert.True(r.IsError);
        Assert.Contains("disabled", Text(r), StringComparison.Ordinal);
        Assert.DoesNotContain(_backend.Calls, c => c.StartsWith("shell", StringComparison.Ordinal));
        lock (_calls)
        {
            Assert.Contains(_calls, c => c.Tool == "adb_shell" && !c.Success);
        }
    }

    [Fact]
    public async Task Screenshot_returns_an_image_and_mapping_text()
    {
        await using var client = await ConnectAsync();

        var r = await CallAsync(client, "take_screenshot", new() { ["maxSize"] = 1200 });

        Assert.NotEqual(true, r.IsError);
        var image = Assert.Single(r.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(FakeBackend.TinyPng, image.DecodedData.ToArray());
        var text = Text(r);
        Assert.Contains("540x1200", text, StringComparison.Ordinal);
        Assert.Contains("refWidth=540", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tap_scales_reference_coordinates_to_the_device()
    {
        await using var client = await ConnectAsync();

        var r = await CallAsync(client, "tap", new() { ["x"] = 270, ["y"] = 600, ["refWidth"] = 540, ["refHeight"] = 1200 });

        Assert.NotEqual(true, r.IsError);
        Assert.Contains("tap 540,1200 0", _backend.Calls);
    }

    [Fact]
    public async Task Tap_outside_the_screen_is_an_error()
    {
        await using var client = await ConnectAsync();

        var r = await CallAsync(client, "tap", new() { ["x"] = 5000, ["y"] = 10 });

        Assert.True(r.IsError);
        Assert.Contains("outside the screen", Text(r), StringComparison.Ordinal);
        Assert.DoesNotContain(_backend.Calls, c => c.StartsWith("tap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Press_key_accepts_names_constants_and_numbers()
    {
        await using var client = await ConnectAsync();

        await CallAsync(client, "press_key", new() { ["key"] = "back" });
        await CallAsync(client, "press_key", new() { ["key"] = "KEYCODE_HOME" });
        await CallAsync(client, "press_key", new() { ["key"] = "187" });
        var bad = await CallAsync(client, "press_key", new() { ["key"] = "warp" });

        Assert.Equal(["key 4", "key 3", "key 187"], _backend.Calls.Where(c => c.StartsWith("key", StringComparison.Ordinal)));
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Backend_errors_reach_the_client_as_tool_errors()
    {
        await using var client = await ConnectAsync();
        _backend.Throw = new McpToolException("Device 'x' is not connected.");

        var r = await CallAsync(client, "press_key", new() { ["key"] = "home" });

        Assert.True(r.IsError);
        Assert.Contains("Device 'x' is not connected.", Text(r), StringComparison.Ordinal);

        _backend.Throw = new InvalidOperationException("boom");
        var r2 = await CallAsync(client, "press_key", new() { ["key"] = "home" });
        Assert.True(r2.IsError);
        Assert.Contains("boom", Text(r2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dump_ui_and_tap_element_use_the_hierarchy()
    {
        await using var client = await ConnectAsync();

        var dump = Text(await CallAsync(client, "dump_ui"));
        Assert.Contains("\"Settings\"", dump, StringComparison.Ordinal);
        Assert.Contains("desc=\"Confirm\"", dump, StringComparison.Ordinal);
        Assert.Contains("@(300,2100)", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameLayout", dump, StringComparison.Ordinal);

        var tapped = await CallAsync(client, "tap_element", new() { ["text"] = "confirm" });
        Assert.NotEqual(true, tapped.IsError);
        Assert.Contains("tap 300,2100 0", _backend.Calls);

        var missing = await CallAsync(client, "tap_element", new() { ["text"] = "Nope" });
        Assert.True(missing.IsError);
        Assert.Contains("\"Settings\"", Text(missing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scroll_down_swipes_upwards_through_the_centre()
    {
        await using var client = await ConnectAsync();

        await CallAsync(client, "scroll", new() { ["direction"] = "down" });

        Assert.Contains("swipe 540,1800 540,600 400", _backend.Calls);
    }

    [Fact]
    public async Task Adb_shell_output_is_truncated()
    {
        await using var client = await ConnectAsync();
        _backend.ShellOutput = new string('x', ClickZenTools.MaxShellOutputChars + 100);

        var text = Text(await CallAsync(client, "adb_shell", new() { ["command"] = "cat big", ["asRoot"] = true }));

        Assert.Contains("output truncated", text, StringComparison.Ordinal);
        Assert.True(text.Length < ClickZenTools.MaxShellOutputChars + 100);
        Assert.Contains("shell cat big root=True", _backend.Calls);
    }

    [Fact]
    public async Task Set_variable_keeps_json_types()
    {
        await using var client = await ConnectAsync();

        await CallAsync(client, "set_variable", new() { ["name"] = "count", ["value"] = 3 });
        await CallAsync(client, "set_variable", new() { ["name"] = "state", ["value"] = "battle" });
        var bad = await CallAsync(client, "set_variable", new() { ["name"] = "1bad", ["value"] = 1 });

        Assert.Equal(3L, _backend.Globals.Get("count").AsInt);
        Assert.Equal("battle", _backend.Globals.Get("state").AsString);
        Assert.True(bad.IsError);

        var vars = Text(await CallAsync(client, "get_variables"));
        using var doc = JsonDocument.Parse(vars);
        Assert.Equal(3, doc.RootElement.GetProperty("global").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Calls_are_reported_with_client_and_arguments()
    {
        await using var client = await ConnectAsync();

        await CallAsync(client, "launch_app", new() { ["package"] = "com.demo.game" });
        var bad = await CallAsync(client, "launch_app", new() { ["package"] = "not a package" });

        Assert.True(bad.IsError);
        lock (_calls)
        {
            var ok = Assert.Single(_calls, c => c.Tool == "launch_app" && c.Success);
            Assert.Contains("package=\"com.demo.game\"", ok.Arguments, StringComparison.Ordinal);
            Assert.Contains(_calls, c => c.Tool == "launch_app" && !c.Success && c.Error!.Contains("not a valid package", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Automation_and_playback_tools_forward_arguments()
    {
        await using var client = await ConnectAsync();

        var start = await CallAsync(client, "start_automation", new() { ["devices"] = new[] { "emulator-5554" } });
        await CallAsync(client, "play_recording", new() { ["recording"] = "daily.czrec", ["speed"] = 2.0, ["loops"] = 3, ["wait"] = true });
        var log = Text(await CallAsync(client, "get_automation_log", new() { ["max"] = 5 }));

        Assert.NotEqual(true, start.IsError);
        Assert.Contains("start emulator-5554", _backend.Calls);
        Assert.Contains("play daily.czrec 2 3 wait=True", _backend.Calls);
        Assert.Contains("Task Collect fired", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_image_passes_image_scale_and_rejects_bad_values()
    {
        await using var client = await ConnectAsync();
        var b64 = Convert.ToBase64String(FakeBackend.TinyPng);

        var ok = await CallAsync(client, "find_image", new() { ["imageBase64"] = b64, ["imageScale"] = 1.875 });
        var bad = await CallAsync(client, "find_image", new() { ["imageBase64"] = b64, ["imageScale"] = 50 });

        Assert.NotEqual(true, ok.IsError);
        Assert.Contains(_backend.Calls, c => c.Contains("scale=1.875", StringComparison.Ordinal));
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Results_keep_non_ascii_text_readable()
    {
        await using var client = await ConnectAsync();

        await CallAsync(client, "set_variable", new() { ["name"] = "state", ["value"] = "战斗中" });
        var text = Text(await CallAsync(client, "get_variables"));

        Assert.Contains("战斗中", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaled_screenshot_text_explains_find_image_scale()
    {
        await using var client = await ConnectAsync();

        var text = Text(await CallAsync(client, "take_screenshot", new() { ["maxSize"] = 1200 }));

        Assert.Contains("imageScale=2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Find_image_accepts_base64_with_data_url_prefix()
    {
        await using var client = await ConnectAsync();

        var r = await CallAsync(client, "find_image", new()
        {
            ["imageBase64"] = "data:image/png;base64," + Convert.ToBase64String(FakeBackend.TinyPng),
            ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 50,
        });

        Assert.NotEqual(true, r.IsError);
        Assert.Contains(_backend.Calls, c => c.StartsWith($"find  png={FakeBackend.TinyPng.Length} area=(0, 0, 100x50)", StringComparison.Ordinal));
        var bad = await CallAsync(client, "find_image", new() { ["imageBase64"] = "aGVsbG8=" });
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Emulator_tools_are_listed_in_the_observe_group()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        foreach (var name in new[] { "list_emulators", "connect_emulator", "bind_emulator_window" })
        {
            var t = tools.Single(x => x.Name == name).ProtocolTool;
            Assert.Equal(McpCapability.Observe, ClickZenTools.CapabilityOf(name));
            Assert.False(t.Annotations?.DestructiveHint);
            Assert.Contains("emulator", t.Description, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(tools.Single(x => x.Name == "list_emulators").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools.Single(x => x.Name == "connect_emulator").ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task List_emulators_returns_instances_with_their_adb_address()
    {
        await using var client = await ConnectAsync();

        var r = await CallAsync(client, "list_emulators");

        Assert.NotEqual(true, r.IsError);
        using var doc = System.Text.Json.JsonDocument.Parse(Text(r));
        var mumu = doc.RootElement.GetProperty("running")[0];
        Assert.Equal("mumu:MuMuPlayer-12.0-0", mumu.GetProperty("id").GetString());
        Assert.Equal("127.0.0.1:16384", mumu.GetProperty("adbAddress").GetString());
        Assert.Equal("config", mumu.GetProperty("portSource").GetString());
        Assert.Equal("notConnected", mumu.GetProperty("state").GetString());
        Assert.Contains("MuMu 模拟器", mumu.GetProperty("name").GetString(), StringComparison.Ordinal); // CJK stays readable
    }

    [Fact]
    public async Task List_emulators_explains_when_none_runs()
    {
        await using var client = await ConnectAsync();
        _backend.Emulators.Clear();

        var installed = Text(await CallAsync(client, "list_emulators"));
        Assert.Contains("No emulator is running", installed, StringComparison.Ordinal);
        Assert.Contains("mumu", installed, StringComparison.Ordinal);

        _backend.InstalledEmulators.Clear();
        Assert.Contains("none is installed", Text(await CallAsync(client, "list_emulators")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_emulator_connects_and_reports_failures_as_errors()
    {
        await using var client = await ConnectAsync();

        var ok = await CallAsync(client, "connect_emulator", new() { ["emulator"] = "MuMuPlayer-12.0-0", ["makeCurrent"] = false });
        Assert.NotEqual(true, ok.IsError);
        Assert.Contains("\"device\":\"127.0.0.1:16384\"", Text(ok), StringComparison.Ordinal);
        Assert.Contains("connect-emulator MuMuPlayer-12.0-0 False", _backend.Calls);

        _backend.Emulators[0] = _backend.Emulators[0] with { AdbAddress = null, State = "portUnknown" };
        var failed = await CallAsync(client, "connect_emulator");
        Assert.True(failed.IsError);
        Assert.Contains("port unknown", Text(failed), StringComparison.Ordinal);
        Assert.Contains("connect-emulator  True", _backend.Calls); // omitted emulator / default makeCurrent

        var unknown = await CallAsync(client, "connect_emulator", new() { ["emulator"] = "nox:Nox_9" });
        Assert.True(unknown.IsError);
        Assert.Contains("No emulator", Text(unknown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bind_emulator_window_returns_the_window_device()
    {
        await using var client = await ConnectAsync();

        var r = await CallAsync(client, "bind_emulator_window", new() { ["emulator"] = "mumu:MuMuPlayer-12.0-0" });

        Assert.NotEqual(true, r.IsError);
        var text = Text(r);
        Assert.Contains("\"windowDevice\":\"window:abc123\"", text, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"screenshotMatch\"", text, StringComparison.Ordinal);
        Assert.Contains("bind-emulator mumu:MuMuPlayer-12.0-0", _backend.Calls);
    }
}
