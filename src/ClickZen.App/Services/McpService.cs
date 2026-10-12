using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClickZen.Core.Settings;
using ClickZen.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.Services;

/// <summary>
/// Owns the MCP server (AI access): started/stopped from settings independent of any page, token stored
/// DPAPI-protected (current user), capability switches read live from settings, recent tool calls kept for
/// the AI page. Observable properties change on the UI thread.
/// </summary>
public sealed partial class McpService : ObservableObject, IAsyncDisposable
{
    public const int MaxCallRecords = 200;

    private static readonly byte[] Entropy = "ClickZen.Mcp.v1"u8.ToArray();

    private readonly SettingsService _settings;
    private readonly McpBackend _backend;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<McpService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpServerHost? _host;
    private DispatcherQueue? _ui;

    public McpService(SettingsService settings, McpBackend backend, ILoggerFactory loggers)
    {
        _settings = settings;
        _backend = backend;
        _loggers = loggers;
        _log = loggers.CreateLogger<McpService>();
    }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial int Port { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    /// <summary>Tool calls since start, all clients.</summary>
    [ObservableProperty]
    public partial int CallCount { get; private set; }

    /// <summary>Newest first, at most <see cref="MaxCallRecords"/>.</summary>
    public ObservableCollection<McpCallRecord> Calls { get; } = [];

    public McpSettings Settings => _settings.Current.Mcp;

    /// <summary>The endpoint local clients use.</summary>
    public string LocalUrl => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{(IsRunning ? Port : Settings.Port)}{McpServerHost.EndpointPath}");

    /// <summary>Starts the server if enabled in settings. Call once from the UI thread.</summary>
    public async Task InitializeAsync(DispatcherQueue ui)
    {
        _ui = ui;
        _backend.Attach(ui);
        if (Settings.Enabled)
        {
            await StartAsync();
        }
    }

    // ------------------------------------------------------------------ token

    /// <summary>The bearer token (generated and stored on first use).</summary>
    public string Token
    {
        get
        {
            var t = Unprotect(Settings.ProtectedToken);
            if (t.Length == 0)
            {
                t = McpServerHost.NewToken();
                _settings.Update(s => s.Mcp.ProtectedToken = Protect(t));
            }

            return t;
        }
    }

    /// <summary>Replaces the token and restarts a running server so old tokens stop working.</summary>
    public async Task RegenerateTokenAsync()
    {
        _settings.Update(s => s.Mcp.ProtectedToken = Protect(McpServerHost.NewToken()));
        OnPropertyChanged(nameof(Token));
        await RestartIfRunningAsync();
    }

    public static string Protect(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "";
        }

        return Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(token), Entropy));
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(stored), Entropy));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // Settings copied from another user/machine – a new token is generated.
        }
    }

    // ------------------------------------------------------------------ capabilities

    public bool Allows(McpCapability capability) => Settings.Allows(capability);

    public void SetAllowed(McpCapability capability, bool allowed)
    {
        if (Settings.Allows(capability) == allowed)
        {
            return;
        }

        _settings.Update(s => s.Mcp.Set(capability, allowed));
        _log.LogInformation("MCP tool group {Capability} {State}", capability, allowed ? "enabled" : "disabled");
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Enables/disables the server, persisting the choice.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        _settings.Update(s => s.Mcp.Enabled = enabled);
        if (enabled)
        {
            await StartAsync();
        }
        else
        {
            await StopAsync();
        }
    }

    /// <summary>Applies port / remote changes by restarting a running server.</summary>
    public async Task RestartIfRunningAsync()
    {
        if (_host is not null)
        {
            await StopAsync();
            await StartAsync();
        }
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_host is not null)
            {
                return;
            }

            var s = Settings;
            var host = new McpServerHost(_backend, Allows, new McpServerHostOptions
            {
                Port = s.Port,
                AllowRemote = s.AllowRemote,
                Token = Token,
            }, _loggers);
            host.ToolCalled += OnToolCalled;
            try
            {
                await host.StartAsync();
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                await host.DisposeAsync();
                _log.LogWarning(ex, "MCP server could not listen on port {Port}", s.Port);
                Ui(() =>
                {
                    LastError = ex.InnerException?.Message ?? ex.Message;
                    IsRunning = false;
                });
                return;
            }

            _host = host;
            Ui(() =>
            {
                LastError = null;
                Port = host.Port;
                IsRunning = true;
                OnPropertyChanged(nameof(LocalUrl));
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var host = _host;
            _host = null;
            if (host is not null)
            {
                host.ToolCalled -= OnToolCalled;
                await host.DisposeAsync();
            }

            Ui(() =>
            {
                IsRunning = false;
                OnPropertyChanged(nameof(LocalUrl));
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ClearCalls() => Ui(Calls.Clear);

    private void OnToolCalled(object? sender, McpCallRecord record)
    {
        if (!record.Success)
        {
            _log.LogInformation("MCP {Tool} from {Client} failed: {Error}", record.Tool, record.Client, record.Error);
        }
        else
        {
            _log.LogDebug("MCP {Tool} from {Client} ({Ms} ms)", record.Tool, record.Client, record.ElapsedMs);
        }

        Ui(() =>
        {
            Calls.Insert(0, record);
            while (Calls.Count > MaxCallRecords)
            {
                Calls.RemoveAt(Calls.Count - 1);
            }

            CallCount++;
        });
    }

    private void Ui(Action action)
    {
        if (_ui is null || _ui.HasThreadAccess)
        {
            action();
        }
        else
        {
            _ui.TryEnqueue(() => action());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }
}
