using System.Text.Json;
using ClickZen.App.Services;
using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using ClickZen.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.App;

/// <summary>
/// Entry point. <c>ClickZen.exe --mcp-stdio</c> runs the headless MCP stdio bridge (for AI clients that only
/// launch stdio servers); anything else starts the WinUI application as usual.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals(McpClientConfig.StdioArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return RunStdioBridge();
        }

        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }

    /// <summary>
    /// Bridges stdin/stdout to the running instance's MCP server. Reads port and token from the local settings
    /// (read-only: never writes them). stdout carries only protocol messages; diagnostics go to stderr.
    /// </summary>
    private static int RunStdioBridge()
    {
        using var loggers = LoggerFactory.Create(b => b
            .AddProvider(new StderrLoggerProvider())
            .SetMinimumLevel(Environment.GetEnvironmentVariable("CLICKZEN_MCP_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Warning));
        var log = loggers.CreateLogger("ClickZen.Mcp.Stdio");
        try
        {
            var paths = new AppPaths(Environment.GetEnvironmentVariable("CLICKZEN_DATA_DIR"));
            var mcp = ReadMcpSettings(paths.SettingsFile);
            var token = McpService.Unprotect(mcp.ProtectedToken);
            string? reason = null;
            if (!mcp.Enabled)
            {
                reason = "The MCP server is disabled in ClickZen. Open ClickZen → AI page and switch on \"MCP server\", then retry.";
            }
            else if (token.Length == 0)
            {
                reason = "ClickZen has no MCP token yet. Open ClickZen → AI page once to create it, then retry.";
            }

            var exe = Environment.ProcessPath;
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            var bridge = new McpStdioBridge(new McpBridgeOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{mcp.Port}{McpServerHost.EndpointPath}"),
                Token = token,
                LaunchExecutable = Environment.GetEnvironmentVariable("CLICKZEN_MCP_NO_LAUNCH") == "1" ? null : exe,
                UnavailableReason = reason,
            }, loggers);
            try
            {
                bridge.RunStdioAsync(cts.Token).GetAwaiter().GetResult();
            }
            finally
            {
                bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "MCP stdio bridge failed");
            return 1;
        }
    }

    /// <summary>The MCP section of settings.json, without creating or rewriting the file.</summary>
    internal static McpSettings ReadMcpSettings(string settingsFile)
    {
        try
        {
            if (File.Exists(settingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile), JsonDefaults.Options);
                if (s?.Mcp is { } m)
                {
                    m.Port = Math.Clamp(m.Port, 1024, 65535);
                    m.ProtectedToken ??= "";
                    return m;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ClickZen: could not read {settingsFile}: {ex.Message}");
        }

        return new McpSettings();
    }

    private sealed class StderrLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

        public void Dispose()
        {
        }

        private sealed class StderrLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var line = $"[{logLevel}] {category}: {formatter(state, exception)}";
                if (exception is not null)
                {
                    line += " – " + exception.Message;
                }

                Console.Error.WriteLine(line);
            }
        }
    }
}
