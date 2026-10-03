using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ClickZen.Core.Automation;
using ClickZen.Core.Settings;
using ClickZen.Core.Variables;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.Services;

/// <summary>
/// Owns the <see cref="VariableSyncServer"/>: started/stopped from settings (independent of any page),
/// exposes the global variable store plus the sync direction of each name, and keeps the client list
/// observable for the Variables page. The token is stored DPAPI-protected (current user).
/// </summary>
public sealed partial class VariableSyncService : ObservableObject, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly VariableStore _globals;
    private readonly ILogger<VariableSyncService> _log;
    private readonly ILoggerFactory _loggers;
    private readonly Dictionary<string, SyncDirection> _directions = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private VariableSyncServer? _server;
    private DispatcherQueue? _ui;

    public VariableSyncService(SettingsService settings, VariableStore globals, ILoggerFactory loggers)
    {
        _settings = settings;
        _globals = globals;
        _loggers = loggers;
        _log = loggers.CreateLogger<VariableSyncService>();
    }

    /// <summary>Variables shared by every device run; the sync server exposes this store.</summary>
    public VariableStore Globals => _globals;

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial int Port { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<VariableSyncClientInfo> Clients { get; private set; } = [];

    /// <summary>Raised when the direction table changes (a scheme was loaded or edited).</summary>
    public event EventHandler? DirectionsChanged;

    /// <summary>Starts the server if enabled in settings. Call once from the UI thread.</summary>
    public async Task InitializeAsync(DispatcherQueue ui)
    {
        _ui = ui;
        if (_settings.Current.Sync.Enabled)
        {
            await StartAsync();
        }
    }

    // ------------------------------------------------------------------ directions

    public SyncDirection DirectionOf(string name)
    {
        lock (_lock)
        {
            return _directions.GetValueOrDefault(name, SyncDirection.None);
        }
    }

    public IReadOnlyDictionary<string, SyncDirection> Directions
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, SyncDirection>(_directions, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Takes the sync directions declared by a scheme. Global-scope variables are declared in the
    /// shared store immediately so remote clients can read/write them before the scheme runs.
    /// </summary>
    public void ApplyScheme(Scheme? scheme)
    {
        lock (_lock)
        {
            _directions.Clear();
            foreach (var def in scheme?.Variables ?? [])
            {
                if (!string.IsNullOrWhiteSpace(def.Name) && def.Sync != SyncDirection.None)
                {
                    _directions[def.Name] = def.Sync;
                }
            }
        }

        foreach (var def in scheme?.Variables ?? [])
        {
            if (def.Scope == VariableScope.Global && !string.IsNullOrWhiteSpace(def.Name) && !_globals.Contains(def.Name))
            {
                _globals.Declare(def.Name, def.Type, def.Initial.ConvertTo(def.Type));
            }
        }

        DirectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ token

    /// <summary>The plain token (empty = no authentication).</summary>
    public string Token
    {
        get => Unprotect(_settings.Current.Sync.ProtectedToken);
        set
        {
            _settings.Update(s => s.Sync.ProtectedToken = Protect(value ?? ""));
            OnPropertyChanged();
        }
    }

    public static string Protect(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "";
        }

        var bytes = Dpapi.Protect(Encoding.UTF8.GetBytes(token), Entropy);
        return Convert.ToBase64String(bytes);
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
            return ""; // Settings copied from another user/machine – treat as unset.
        }
    }

    private static readonly byte[] Entropy = "ClickZen.VariableSync.v1"u8.ToArray();

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Enables/disables the service, persisting the choice.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        _settings.Update(s => s.Sync.Enabled = enabled);
        if (enabled)
        {
            await StartAsync();
        }
        else
        {
            await StopAsync();
        }
    }

    /// <summary>Applies port/token/limit changes by restarting a running server.</summary>
    public async Task RestartIfRunningAsync()
    {
        if (_server is not null)
        {
            await StopAsync();
            await StartAsync();
        }
    }

    public async Task StartAsync()
    {
        if (_server is not null)
        {
            return;
        }

        var s = _settings.Current.Sync;
        var server = new VariableSyncServer(_globals, DirectionOf, new VariableSyncOptions
        {
            Port = s.Port,
            Token = Token,
            MaxClients = Math.Max(1, s.MaxClients),
        }, _loggers.CreateLogger<VariableSyncServer>());
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            await server.DisposeAsync();
            _log.LogWarning(ex, "Variable sync server could not listen on port {Port}", s.Port);
            Ui(() =>
            {
                LastError = ex.Message;
                IsRunning = false;
            });
            return;
        }

        server.ClientsChanged += (_, _) => Ui(() => Clients = server.Clients);
        _server = server;
        Ui(() =>
        {
            LastError = null;
            Port = server.Port;
            IsRunning = true;
            Clients = [];
        });
    }

    public async Task StopAsync()
    {
        var server = _server;
        _server = null;
        if (server is not null)
        {
            await server.DisposeAsync();
        }

        Ui(() =>
        {
            IsRunning = false;
            Clients = [];
        });
    }

    /// <summary>IPv4 addresses of active, non-loopback interfaces (shown so users know where to connect).</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
