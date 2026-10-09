using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClickZen.Mcp;

/// <summary>AI clients the AI page can produce a ready-to-paste configuration for.</summary>
public enum McpClientKind
{
    /// <summary>Claude Code CLI command (<c>claude mcp add</c>).</summary>
    ClaudeCode,
    /// <summary>Cursor <c>~/.cursor/mcp.json</c>.</summary>
    Cursor,
    /// <summary>VS Code <c>.vscode/mcp.json</c>.</summary>
    VsCode,
    /// <summary>Claude Desktop <c>claude_desktop_config.json</c> (stdio bridge).</summary>
    ClaudeDesktop,
    /// <summary>OpenAI Codex CLI <c>~/.codex/config.toml</c> (stdio bridge).</summary>
    Codex,
    /// <summary>Generic Streamable HTTP JSON (<c>.mcp.json</c> style, most clients).</summary>
    GenericHttp,
}

/// <summary>Builds MCP client configuration snippets (pure, tested).</summary>
public static class McpClientConfig
{
    public const string ServerKey = "clickzen";
    public const string StdioArgument = "--mcp-stdio";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <param name="url">Streamable HTTP endpoint, e.g. http://127.0.0.1:9528/mcp.</param>
    /// <param name="token">Bearer token.</param>
    /// <param name="exePath">ClickZen.exe, for stdio-only clients.</param>
    public static string Build(McpClientKind kind, string url, string token, string exePath)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(exePath);
        var auth = "Bearer " + token;
        return kind switch
        {
            McpClientKind.ClaudeCode =>
                $"claude mcp add --transport http {ServerKey} {url} --header \"Authorization: {auth}\"",
            McpClientKind.Cursor => Json(new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [ServerKey] = new JsonObject
                    {
                        ["url"] = url,
                        ["headers"] = new JsonObject { ["Authorization"] = auth },
                    },
                },
            }),
            McpClientKind.VsCode => Json(new JsonObject
            {
                ["servers"] = new JsonObject
                {
                    [ServerKey] = new JsonObject
                    {
                        ["type"] = "http",
                        ["url"] = url,
                        ["headers"] = new JsonObject { ["Authorization"] = auth },
                    },
                },
            }),
            McpClientKind.ClaudeDesktop => Json(new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [ServerKey] = new JsonObject
                    {
                        ["command"] = exePath,
                        ["args"] = new JsonArray(StdioArgument),
                    },
                },
            }),
            McpClientKind.Codex =>
                $"[mcp_servers.{ServerKey}]\ncommand = {TomlString(exePath)}\nargs = [\"{StdioArgument}\"]\n",
            _ => Json(new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [ServerKey] = new JsonObject
                    {
                        ["type"] = "http",
                        ["url"] = url,
                        ["headers"] = new JsonObject { ["Authorization"] = auth },
                    },
                },
            }),
        };
    }

    /// <summary>Whether the client connects through the stdio bridge (no token in its config).</summary>
    public static bool UsesStdio(McpClientKind kind) => kind is McpClientKind.ClaudeDesktop or McpClientKind.Codex;

    private static string Json(JsonObject o) => o.ToJsonString(Indented);

    /// <summary>TOML basic string (escapes backslashes and quotes).</summary>
    internal static string TomlString(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
