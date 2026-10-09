using ClickZen.Core.Geometry;
using ClickZen.Core.Settings;

namespace ClickZen.Mcp.Tests;

public sealed class HelperTests
{
    [Theory]
    [InlineData("home", 3)]
    [InlineData("BACK", 4)]
    [InlineData("recents", 187)]
    [InlineData("app-switch", 187)]
    [InlineData("KEYCODE_ENTER", 66)]
    [InlineData("volume up", 24)]
    [InlineData("26", 26)]
    public void Key_names_resolve(string name, int code)
    {
        Assert.True(KeyNames.TryResolve(name, out var c));
        Assert.Equal(code, c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("warp")]
    [InlineData("0")]
    [InlineData("99999")]
    public void Unknown_keys_are_rejected(string name) => Assert.False(KeyNames.TryResolve(name, out _));

    [Fact]
    public void Ui_hierarchy_parses_nested_nodes_and_skips_layout()
    {
        var xml = new FakeBackend().UiXml;

        var nodes = UiHierarchy.Parse(xml);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("Settings", nodes[0].Text);
        Assert.Equal(new RectI(40, 100, 360, 80), nodes[0].Bounds);
        Assert.Equal(1, nodes[0].Depth);
        Assert.True(nodes[1].Clickable);
        Assert.Equal("Confirm", nodes[1].ContentDesc);
        Assert.Equal(new PointI(300, 2100), nodes[1].Center);
        Assert.Equal(3, UiHierarchy.Parse(xml, includeAll: true).Count);
    }

    [Fact]
    public void Ui_hierarchy_scales_bounds()
    {
        var nodes = UiHierarchy.Parse(new FakeBackend().UiXml, 0.5, 0.5);

        Assert.Equal(new RectI(50, 1000, 200, 100), nodes[1].Bounds);
    }

    [Fact]
    public void Ui_hierarchy_reports_garbage_as_tool_error()
    {
        var ex = Assert.Throws<McpToolException>(() => UiHierarchy.Parse("ERROR: could not get idle state."));
        Assert.Contains("could not get idle state", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ui_hierarchy_filter_and_format()
    {
        var nodes = UiHierarchy.Parse(new FakeBackend().UiXml);

        var f = UiHierarchy.Filter(nodes, "ok");
        var text = UiHierarchy.Format(f, 10);

        Assert.Single(f);
        Assert.Contains("id=ok", text, StringComparison.Ordinal);
        Assert.Contains("{clickable}", text, StringComparison.Ordinal);
        Assert.Contains("[100,2000][500,2200]", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[0,0][1080,2400]", 0, 0, 1080, 2400)]
    [InlineData("[-5,10][20,30]", -5, 10, 25, 20)]
    public void Bounds_parse(string text, int x, int y, int w, int h)
    {
        Assert.True(UiHierarchy.TryParseBounds(text, out var r));
        Assert.Equal(new RectI(x, y, w, h), r);
    }

    [Fact]
    public void Coordinates_scale_from_a_reference_image()
    {
        var screen = new SizeI(1080, 2400);

        Assert.Equal(new PointI(540, 1200), CoordinateScaling.ToDevice(270, 600, 540, 1200, screen));
        Assert.Equal(new PointI(270, 600), CoordinateScaling.ToDevice(270, 600, null, null, screen));
        Assert.Equal(new RectI(0, 0, 1080, 2400), CoordinateScaling.ToDevice(new RectI(0, 0, 540, 1200), 540, 1200, screen));
        Assert.Throws<McpToolException>(() => CoordinateScaling.EnsureOnScreen(new PointI(1080, 0), screen));
        CoordinateScaling.EnsureOnScreen(new PointI(1079, 2399), screen);
    }

    [Fact]
    public void Every_tool_has_a_capability_and_snake_case_name()
    {
        Assert.True(ClickZenTools.Capabilities.Count >= 30);
        foreach (var (name, _) in ClickZenTools.Capabilities)
        {
            Assert.Matches("^[a-z][a-z0-9_]*$", name);
        }

        Assert.Equal(McpCapability.Shell, ClickZenTools.CapabilityOf("adb_shell"));
        Assert.Equal(McpCapability.Input, ClickZenTools.CapabilityOf("tap"));
        Assert.Equal(McpCapability.Observe, ClickZenTools.CapabilityOf("take_screenshot"));
        foreach (var c in McpCapabilities.All)
        {
            Assert.Contains(c, ClickZenTools.Capabilities.Values);
        }
    }

    [Fact]
    public void Capability_switches_round_trip_through_settings()
    {
        var s = new McpSettings();
        foreach (var c in McpCapabilities.All)
        {
            Assert.True(s.Allows(c));
            s.Set(c, false);
            Assert.False(s.Allows(c));
        }

        Assert.False(s.AllowShell || s.AllowObserve || s.AllowInput || s.AllowAutomation || s.AllowRecording || s.AllowVariables);
    }

    [Fact]
    public void Client_configs_contain_url_and_token()
    {
        const string url = "http://127.0.0.1:9528/mcp";
        const string token = "abc";
        const string exe = @"C:\Apps\ClickZen\ClickZen.exe";

        var claude = McpClientConfig.Build(McpClientKind.ClaudeCode, url, token, exe);
        Assert.Equal("claude mcp add --transport http clickzen http://127.0.0.1:9528/mcp --header \"Authorization: Bearer abc\"", claude);

        foreach (var kind in new[] { McpClientKind.Cursor, McpClientKind.VsCode, McpClientKind.GenericHttp })
        {
            var json = McpClientConfig.Build(kind, url, token, exe);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Contains(url, json, StringComparison.Ordinal);
            Assert.Contains("Bearer abc", json, StringComparison.Ordinal);
            Assert.False(McpClientConfig.UsesStdio(kind));
        }

        var desktop = McpClientConfig.Build(McpClientKind.ClaudeDesktop, url, token, exe);
        using (var doc = System.Text.Json.JsonDocument.Parse(desktop))
        {
            var server = doc.RootElement.GetProperty("mcpServers").GetProperty("clickzen");
            Assert.Equal(exe, server.GetProperty("command").GetString());
            Assert.Equal("--mcp-stdio", server.GetProperty("args")[0].GetString());
        }

        Assert.DoesNotContain(token, desktop, StringComparison.Ordinal);
        var codex = McpClientConfig.Build(McpClientKind.Codex, url, token, exe);
        Assert.Contains("command = \"C:\\\\Apps\\\\ClickZen\\\\ClickZen.exe\"", codex, StringComparison.Ordinal);
        Assert.True(McpClientConfig.UsesStdio(McpClientKind.Codex));
    }

    [Fact]
    public void Argument_summary_elides_images_and_long_text()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"x":10,"imageBase64":"AAAAAAAAAA","text":"hello\nworld","flag":true}""");
        var args = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var s = McpServerHost.SummarizeArguments(args);

        Assert.Equal("x=10, imageBase64=<10 chars>, text=\"hello world\", flag=true", s);
    }

    [Fact]
    public void Tokens_are_random_and_url_safe()
    {
        var a = McpServerHost.NewToken();
        var b = McpServerHost.NewToken();

        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
    }
}
