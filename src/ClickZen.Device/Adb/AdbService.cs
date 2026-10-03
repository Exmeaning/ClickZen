using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using ClickZen.Core.Devices;
using ClickZen.Device.Adb.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Adb;

/// <summary>
/// The one entry point for talking to adb. Thread-safe; intended to be a DI singleton.
/// Every method runs its blocking socket work off the caller's thread, maps failures to
/// <see cref="AdbException"/> and lets <see cref="OperationCanceledException"/> through when the caller cancels.
/// </summary>
public sealed class AdbService : IDisposable
{
    public static readonly TimeSpan DefaultShellTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultRootCheckTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan EmulatorProbeTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HostCommandTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PushTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Build properties practically never change while a device stays attached.</summary>
    public static readonly TimeSpan PropertiesCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary><c>wm size</c> / <c>wm density</c> can be changed by the user at any time.</summary>
    public static readonly TimeSpan DisplayCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Default permissions for pushed files (0644).</summary>
    public const int DefaultPushMode = 0x1A4;

    private const string DisplayCommand = "wm size; wm density";
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan EnrichTimeout = TimeSpan.FromSeconds(10);

    private readonly IAdbClient _client;
    private readonly AdbServerHost? _host;
    private readonly ILogger _logger;
    private readonly AsyncTtlCache<string, DeviceProps> _props;
    private readonly AsyncTtlCache<string, (WmSize Size, WmDensity Density)> _display;
    private readonly ConcurrentDictionary<string, SuStyle> _suStyles = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    public AdbService(AdbServerHost host, ILogger<AdbService>? logger = null)
        : this(new AdbClient((host ?? throw new ArgumentNullException(nameof(host))).EndPoint), host, logger, null)
    {
    }

    internal AdbService(IAdbClient client, AdbServerHost? host, ILogger? logger, TimeProvider? time)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _host = host;
        _logger = logger ?? NullLogger.Instance;
        _props = new AsyncTtlCache<string, DeviceProps>(time, StringComparer.Ordinal);
        _display = new AsyncTtlCache<string, (WmSize, WmDensity)>(time, StringComparer.Ordinal);
    }

    /// <summary>The underlying AdvancedSharpAdbClient client, for advanced callers (scrcpy tunnel, file browser…).</summary>
    public IAdbClient Client => _client;

    /// <summary>Endpoint of the adb server this service talks to.</summary>
    public EndPoint EndPoint => _client.EndPoint;

    /// <summary>
    /// When a call fails because no server is listening, start it via <see cref="AdbServerHost"/> and retry once.
    /// </summary>
    public bool AutoStartServer { get; set; } = true;

    // ------------------------------------------------------------------ devices

    /// <summary>
    /// Lists devices known to the adb server. Online devices are enriched with brand/model/Android version
    /// and display size/density; queries for different devices run in parallel and are cached per serial.
    /// A device whose properties cannot be read is still returned (with blank properties).
    /// </summary>
    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default)
    {
        var raw = await RunAsync(null, "list devices", HostCommandTimeout, t => _client.GetDevicesAsync(t), ct);
        var basic = raw.Where(d => !string.IsNullOrEmpty(d.Serial)).Select(ToBasicInfo).ToArray();
        return await Task.WhenAll(basic.Select(d => EnrichAsync(d, ct)));
    }

    /// <summary>
    /// Fills in properties of an online device (cached). Returns <paramref name="device"/> unchanged when it is
    /// not online. Never throws for device-side failures – missing data stays blank.
    /// </summary>
    public async Task<DeviceInfo> EnrichAsync(DeviceInfo device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.State != DeviceAdbState.Online)
        {
            return device;
        }

        var serial = device.Serial;
        var propsTask = _props.GetOrAddAsync(serial, LoadPropsAsync, PropertiesCacheTtl);
        var displayTask = _display.GetOrAddAsync(serial, LoadDisplayAsync, DisplayCacheTtl);

        var result = device;
        try
        {
            var p = await propsTask.WaitAsync(ct);
            result = result with { Brand = p.Brand, Model = p.Model, AndroidVersion = p.AndroidVersion, SdkLevel = p.SdkLevel };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read properties of {Serial}", serial);
        }

        try
        {
            var (size, density) = await displayTask.WaitAsync(ct);
            result = result with { PhysicalSize = size.Effective, Density = density.Effective };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read display info of {Serial}", serial);
        }

        return result;
    }

    /// <summary>Reads <c>wm size</c> (bypassing the cache) – physical and override sizes separately.</summary>
    public async Task<WmSize> GetDisplaySizeAsync(string serial, CancellationToken ct = default)
    {
        _display.Invalidate(serial);
        var (size, _) = await _display.GetOrAddAsync(serial, LoadDisplayAsync, DisplayCacheTtl).WaitAsync(ct);
        return size;
    }

    /// <summary>Reads <c>wm density</c> (bypassing the cache).</summary>
    public async Task<WmDensity> GetDisplayDensityAsync(string serial, CancellationToken ct = default)
    {
        _display.Invalidate(serial);
        var (_, density) = await _display.GetOrAddAsync(serial, LoadDisplayAsync, DisplayCacheTtl).WaitAsync(ct);
        return density;
    }

    /// <summary>Drops cached properties for one device (e.g. after it disconnected), or for all when <paramref name="serial"/> is null.</summary>
    public void InvalidateCache(string? serial = null)
    {
        if (serial is null)
        {
            _props.Clear();
            _display.Clear();
            _suStyles.Clear();
            return;
        }

        _props.Invalidate(serial);
        _display.Invalidate(serial);
        _suStyles.TryRemove(serial, out _);
    }

    /// <summary>Drops only the cached display size/density (call after rotation-independent <c>wm size</c> changes).</summary>
    public void InvalidateDisplayCache(string? serial = null)
    {
        if (serial is null)
        {
            _display.Clear();
        }
        else
        {
            _display.Invalidate(serial);
        }
    }

    // ------------------------------------------------------------------ shell

    /// <summary>
    /// Runs <paramref name="command"/> with <c>adb shell</c> and returns its complete output (lines joined with
    /// <c>\n</c>, no trailing newline). Default timeout 15 s → <see cref="AdbErrorKind.Timeout"/>.
    /// </summary>
    public Task<string> ShellAsync(string serial, string command, CancellationToken ct = default) =>
        ShellAsync(serial, command, DefaultShellTimeout, ct);

    /// <summary>Like <see cref="ShellAsync(string, string, CancellationToken)"/> with an explicit timeout (<see cref="Timeout.InfiniteTimeSpan"/> = none).</summary>
    public Task<string> ShellAsync(string serial, string command, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(serial, "shell", timeout, async t =>
        {
            var receiver = new CollectingReceiver();
            await _client.ExecuteRemoteCommandAsync(command, Device(serial), receiver, Utf8, t);
            t.ThrowIfCancellationRequested();
            return receiver.Output;
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="command"/> as root. The command is single-quoted for <c>sh</c>, so it may contain
    /// quotes, pipes and redirections. Uses the <c>su</c> syntax detected by <see cref="CheckRootAsync(string, CancellationToken)"/>.
    /// </summary>
    public Task<string> RootShellAsync(string serial, string command, CancellationToken ct = default) =>
        RootShellAsync(serial, command, DefaultShellTimeout, ct);

    /// <summary>Like <see cref="RootShellAsync(string, string, CancellationToken)"/> with an explicit timeout.</summary>
    public Task<string> RootShellAsync(string serial, string command, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentNullException.ThrowIfNull(command);
        var style = _suStyles.TryGetValue(serial, out var s) ? s : SuStyle.DashC;
        return ShellAsync(serial, ShellQuoting.BuildRootCommand(command, style), timeout, ct);
    }

    /// <summary>
    /// Probes root with <c>su -c id</c> (falling back to AOSP's <c>su 0 id</c>). A timeout usually means the
    /// root manager on the phone is waiting for the user to allow access: the result is
    /// <see cref="RootStatus.Timeout"/> rather than an exception. Default timeout 20 s.
    /// </summary>
    public Task<RootCheckResult> CheckRootAsync(string serial, CancellationToken ct = default) =>
        CheckRootAsync(serial, DefaultRootCheckTimeout, ct);

    /// <summary>Like <see cref="CheckRootAsync(string, CancellationToken)"/> with an explicit timeout.</summary>
    public async Task<RootCheckResult> CheckRootAsync(string serial, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        var limit = timeout;
        string output;
        try
        {
            output = await ShellAsync(serial, ShellQuoting.BuildRootCommand("id", SuStyle.DashC), limit, ct);
            if (!RootParser.IsRoot(output) && LooksLikeAospSu(output))
            {
                var alt = await ShellAsync(serial, ShellQuoting.BuildRootCommand("id", SuStyle.Uid0), limit, ct);
                if (RootParser.IsRoot(alt))
                {
                    _suStyles[serial] = SuStyle.Uid0;
                    return new RootCheckResult(true, RootStatus.Rooted, alt);
                }

                output = output + "\n" + alt;
            }
        }
        catch (AdbException ex) when (ex.Kind == AdbErrorKind.Timeout)
        {
            return new RootCheckResult(false, RootStatus.Timeout, "");
        }

        var status = RootParser.Classify(output);
        if (status == RootStatus.Rooted)
        {
            _suStyles[serial] = SuStyle.DashC;
        }

        return new RootCheckResult(status == RootStatus.Rooted, status, output);
    }

    /// <summary>
    /// Runs a long-lived command and calls <paramref name="onLine"/> for each line of output (on a background
    /// thread) until the command exits – the task then completes normally – or <paramref name="ct"/> is cancelled,
    /// which closes the shell (the device sends SIGHUP to the process) and throws <see cref="OperationCanceledException"/>.
    /// An exception thrown by <paramref name="onLine"/> stops the command and is rethrown as-is.
    /// </summary>
    public async Task ExecuteStreamingAsync(string serial, string command, Action<string> onLine, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(onLine);
        var receiver = new CallbackReceiver(onLine);
        await RunAsync(serial, "shell (streaming)", Timeout.InfiniteTimeSpan, async t =>
        {
            await _client.ExecuteRemoteCommandAsync(command, Device(serial), receiver, Utf8, t);
            return true;
        }, ct);

        if (receiver.CallbackError is { } error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        }

        ct.ThrowIfCancellationRequested();
    }

    // ------------------------------------------------------------------ connect / pair

    /// <summary>
    /// <c>adb connect host[:port]</c> (default port 5555) through the adb server's <c>host:connect</c> service.
    /// Never throws for network failures – inspect <see cref="AdbConnectResult.Success"/>/<see cref="AdbConnectResult.Kind"/>.
    /// </summary>
    public Task<AdbConnectResult> ConnectAsync(string hostPort, CancellationToken ct = default) =>
        ConnectCoreAsync(hostPort, ConnectTimeout, ct);

    /// <summary>
    /// <c>adb disconnect [host[:port]]</c>. A <see langword="null"/>/empty address disconnects every TCP device.
    /// </summary>
    public async Task<AdbConnectResult> DisconnectAsync(string? hostPort, CancellationToken ct = default)
    {
        string address;
        if (string.IsNullOrWhiteSpace(hostPort))
        {
            address = "";
        }
        else if (HostPortParser.Normalize(hostPort) is { } normalized)
        {
            address = normalized;
        }
        else
        {
            return new AdbConnectResult(false, ConnectResultKind.InvalidAddress, $"Invalid address: {hostPort}", hostPort);
        }

        try
        {
            var reply = await RunAsync(null, "disconnect", HostCommandTimeout, t => address.Length == 0
                ? HostQueryAsync("host:disconnect:", t)
                : _client.DisconnectAsync(address, HostPortParser.DefaultAdbPort, t), ct);
            var parsed = ConnectResultParser.Parse(reply);
            if (parsed.Success)
            {
                if (address.Length == 0)
                {
                    InvalidateCache();
                }
                else
                {
                    InvalidateCache(address);
                }
            }

            return new AdbConnectResult(parsed.Success, parsed.Kind, parsed.Raw, address);
        }
        catch (AdbException ex)
        {
            // adb answers FAIL "no such device 'x'" for unknown addresses.
            var parsed = ConnectResultParser.Parse(ex.InnerException is AdvancedSharpAdbClient.Exceptions.AdbException a ? a.AdbError : ex.Message);
            return new AdbConnectResult(false, parsed.Kind == ConnectResultKind.Unknown ? ConnectResultKind.Failed : parsed.Kind, ex.Message, address);
        }
    }

    /// <summary>
    /// Android 11+ wireless debugging pairing (<c>adb pair host:port code</c>). The pairing port is shown in the
    /// "Pair device with pairing code" dialog and differs from the connection port, so this method does
    /// <b>not</b> connect afterwards – call <see cref="ConnectAsync"/> with the port from the main wireless debugging screen.
    /// </summary>
    public async Task<AdbConnectResult> PairAsync(string hostPort, string code, CancellationToken ct = default)
    {
        if (!HostPortParser.TryParse(hostPort, null, out var host, out var port))
        {
            return new AdbConnectResult(false, ConnectResultKind.InvalidAddress, $"Invalid pairing address (host:port required): {hostPort}", hostPort ?? "");
        }

        var trimmedCode = (code ?? "").Trim();
        if (trimmedCode.Length == 0 || trimmedCode.Any(char.IsWhiteSpace))
        {
            return new AdbConnectResult(false, ConnectResultKind.WrongPairingCode, "Pairing code is empty or invalid.", HostPortParser.Format(host, port));
        }

        var address = HostPortParser.Format(host, port);
        try
        {
            var reply = await RunAsync(null, "pair", PairTimeout, t => _client.PairAsync(address, port, trimmedCode, t), ct);
            var parsed = ConnectResultParser.Parse(reply);
            return new AdbConnectResult(parsed.Success, parsed.Kind, parsed.Raw, address);
        }
        catch (AdbException ex) when (ex.Kind != AdbErrorKind.ServerStartFailed)
        {
            var kind = ex.Kind == AdbErrorKind.Timeout ? ConnectResultKind.Timeout : ConnectResultParser.Parse(ex.Message).Kind;
            return new AdbConnectResult(false, kind == ConnectResultKind.Unknown ? ConnectResultKind.Failed : kind, ex.Message, address);
        }
    }

    /// <summary>
    /// Tries every port in <see cref="EmulatorPresets"/> on 127.0.0.1 in parallel (about 2 s each) and returns
    /// the addresses that are now connected. Ports already served by an <c>emulator-NNNN</c> device are skipped
    /// so the same emulator does not show up twice.
    /// </summary>
    public async Task<IReadOnlyList<string>> ScanEmulatorsAsync(CancellationToken ct = default)
    {
        var skip = new HashSet<int>();
        try
        {
            foreach (var d in await RunAsync(null, "list devices", HostCommandTimeout, t => _client.GetDevicesAsync(t), ct))
            {
                if (DeviceClassifier.EmulatorAdbPort(d.Serial) is int p)
                {
                    skip.Add(p);
                }
            }
        }
        catch (AdbException ex)
        {
            _logger.LogDebug(ex, "Could not list devices before emulator scan");
        }

        var ports = EmulatorPresets.AllPorts.Where(p => !skip.Contains(p)).ToArray();
        var results = await Task.WhenAll(ports.Select(p => ConnectCoreAsync($"127.0.0.1:{p}", EmulatorProbeTimeout, ct)));
        var found = results.Where(r => r.Success).Select(r => r.Address).ToArray();
        _logger.LogInformation("Emulator scan: {Found} of {Count} ports answered ({Addresses})", found.Length, ports.Length, string.Join(", ", found));
        return found;
    }

    // ------------------------------------------------------------------ files / network

    /// <summary>Pushes a local file to the device using the sync service.</summary>
    public async Task PushAsync(string serial, string localPath, string remotePath, int mode = DefaultPushMode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("File to push does not exist.", localPath);
        }

        await RunAsync(serial, "push", PushTimeout, async t =>
        {
            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            // The SyncService constructor opens its connection synchronously; RunAsync already runs us on the pool.
            using var sync = new SyncService(_client, Device(serial));
            await sync.PushAsync(stream, remotePath, (UnixFileStatus)mode, File.GetLastWriteTimeUtc(localPath), null, false, t);
            return true;
        }, ct);
    }

    /// <summary>
    /// The device's Wi-Fi IPv4 address from <c>ip addr show wlan0</c> (falling back to <c>ip route</c>),
    /// or <see langword="null"/> when Wi-Fi is off.
    /// </summary>
    public async Task<string?> GetDeviceIpAsync(string serial, CancellationToken ct = default)
    {
        var addr = await ShellAsync(serial, "ip addr show wlan0 2>/dev/null || ifconfig wlan0 2>/dev/null", ct);
        if (IpParser.ParseWlanIp(addr) is { } ip)
        {
            return ip;
        }

        var route = await ShellAsync(serial, "ip route 2>/dev/null", ct);
        return IpParser.ParseRouteSource(route);
    }

    // ------------------------------------------------------------------ port forwarding

    /// <summary><c>adb forward local remote</c>, e.g. <c>("tcp:27183", "localabstract:scrcpy_1234")</c>. Returns the bound port when <paramref name="local"/> is <c>tcp:0</c>.</summary>
    public Task<int> CreateForwardAsync(string serial, string local, string remote, bool allowRebind = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(local);
        ArgumentException.ThrowIfNullOrEmpty(remote);
        return RunAsync(serial, "forward", HostCommandTimeout, t => _client.CreateForwardAsync(Device(serial), local, remote, allowRebind, t), ct);
    }

    /// <summary><c>adb reverse remote local</c>, e.g. <c>("localabstract:scrcpy_1234", "tcp:27183")</c>.</summary>
    public Task<int> CreateReverseAsync(string serial, string remote, string local, bool allowRebind = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(remote);
        ArgumentException.ThrowIfNullOrEmpty(local);
        return RunAsync(serial, "reverse", HostCommandTimeout, t => _client.CreateReverseForwardAsync(Device(serial), remote, local, allowRebind, t), ct);
    }

    /// <summary><c>adb forward --remove tcp:localPort</c>.</summary>
    public Task RemoveForwardAsync(string serial, int localPort, CancellationToken ct = default) =>
        RunAsync(serial, "forward --remove", HostCommandTimeout, async t =>
        {
            await _client.RemoveForwardAsync(Device(serial), localPort, t);
            return true;
        }, ct);

    /// <summary><c>adb reverse --remove remote</c>.</summary>
    public Task RemoveReverseAsync(string serial, string remote, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(remote);
        return RunAsync(serial, "reverse --remove", HostCommandTimeout, async t =>
        {
            await _client.RemoveReverseForwardAsync(Device(serial), remote, t);
            return true;
        }, ct);
    }

    /// <summary><c>adb forward --remove-all</c> for one device.</summary>
    public Task RemoveAllForwardsAsync(string serial, CancellationToken ct = default) =>
        RunAsync(serial, "forward --remove-all", HostCommandTimeout, async t =>
        {
            await _client.RemoveAllForwardsAsync(Device(serial), t);
            return true;
        }, ct);

    /// <summary><c>adb reverse --remove-all</c> for one device.</summary>
    public Task RemoveAllReversesAsync(string serial, CancellationToken ct = default) =>
        RunAsync(serial, "reverse --remove-all", HostCommandTimeout, async t =>
        {
            await _client.RemoveAllReverseForwardsAsync(Device(serial), t);
            return true;
        }, ct);

    public void Dispose()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _lifetime.Dispose();
    }

    // ------------------------------------------------------------------ internals

    internal static DeviceInfo ToBasicInfo(DeviceData d) => new()
    {
        Serial = d.Serial,
        State = MapState(d.State),
        Kind = DeviceClassifier.Classify(d.Serial),
        Model = (d.Model ?? "").Replace('_', ' '),
    };

    internal static DeviceAdbState MapState(DeviceState state) => state switch
    {
        DeviceState.Online => DeviceAdbState.Online,
        DeviceState.Offline or DeviceState.Connecting => DeviceAdbState.Offline,
        DeviceState.Unauthorized or DeviceState.Authorizing => DeviceAdbState.Unauthorized,
        _ => DeviceAdbState.Other,
    };

    private static DeviceData Device(string serial)
    {
        ArgumentException.ThrowIfNullOrEmpty(serial);
        return new DeviceData { Serial = serial };
    }

    private static bool LooksLikeAospSu(string output) =>
        output.Contains("invalid uid", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Unknown id", StringComparison.OrdinalIgnoreCase)
        || output.Contains("usage: su", StringComparison.OrdinalIgnoreCase);

    private async Task<DeviceProps> LoadPropsAsync(string serial)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var output = await ShellAsync(serial, PropParser.Command, EnrichTimeout, cts.Token);
        return PropParser.Parse(output);
    }

    private async Task<(WmSize, WmDensity)> LoadDisplayAsync(string serial)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var output = await ShellAsync(serial, DisplayCommand, EnrichTimeout, cts.Token);
        return (WmParser.ParseSize(output), WmParser.ParseDensity(output));
    }

    private async Task<AdbConnectResult> ConnectCoreAsync(string hostPort, TimeSpan timeout, CancellationToken ct)
    {
        if (!HostPortParser.TryParse(hostPort, HostPortParser.DefaultAdbPort, out var host, out var port))
        {
            return new AdbConnectResult(false, ConnectResultKind.InvalidAddress, $"Invalid address: {hostPort}", hostPort ?? "");
        }

        var address = HostPortParser.Format(host, port);
        try
        {
            // ASAC uses the host verbatim when it already contains ':' – pass the formatted address so IPv6 works.
            var reply = await RunAsync(null, "connect", timeout, t => _client.ConnectAsync(address, port, t), ct);
            var parsed = ConnectResultParser.Parse(reply);
            if (parsed.Success)
            {
                InvalidateCache(address);
            }

            return new AdbConnectResult(parsed.Success, parsed.Kind, parsed.Raw, address);
        }
        catch (AdbException ex) when (ex.Kind != AdbErrorKind.ServerStartFailed)
        {
            var kind = ex.Kind == AdbErrorKind.Timeout ? ConnectResultKind.Timeout : ConnectResultParser.Parse(ex.Message).Kind;
            return new AdbConnectResult(false, kind == ConnectResultKind.Unknown ? ConnectResultKind.Failed : kind, ex.Message, address);
        }
    }

    /// <summary>Sends a <c>host:</c> request whose reply is OKAY + length-prefixed string.</summary>
    private async Task<string> HostQueryAsync(string request, CancellationToken ct)
    {
        using var socket = new AdbSocket(_client.EndPoint, null);
        await socket.SendAdbRequestAsync(request, ct);
        _ = await socket.ReadAdbResponseAsync(ct);
        return await socket.ReadStringAsync(ct);
    }

    /// <summary>
    /// Runs <paramref name="op"/> on the thread pool (ASAC opens sockets synchronously) with a timeout, mapping
    /// failures to <see cref="AdbException"/>. Starts the server and retries once if it is not running.
    /// </summary>
    private async Task<T> RunAsync<T>(string? serial, string operation, TimeSpan timeout, Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(serial, operation, timeout, op, ct);
            }
            catch (AdbException ex) when (ex.Kind == AdbErrorKind.ConnectionRefused && attempt == 0 && AutoStartServer && _host is not null)
            {
                _logger.LogInformation("adb server not reachable during {Operation}; starting it", operation);
                await _host.EnsureStartedAsync(ct);
            }
        }
    }

    private async Task<T> RunOnceAsync<T>(string? serial, string operation, TimeSpan timeout, Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            cts.CancelAfter(timeout);
        }

        var token = cts.Token;
        try
        {
            // WaitAsync bounds the synchronous connect phase inside ASAC, which ignores the token.
            return await Task.Run(() => op(token), token).WaitAsync(token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            throw new AdbException(AdbErrorKind.Timeout, $"{operation} timed out after {timeout.TotalSeconds:0.#} s", serial, ex);
        }
        catch (Exception ex) when (ex is not AdbException && ex is not OperationCanceledException && !IsUsageError(ex))
        {
            if (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The socket was torn down by our timeout and surfaced as an I/O error.
                throw new AdbException(AdbErrorKind.Timeout, $"{operation} timed out after {timeout.TotalSeconds:0.#} s", serial, ex);
            }

            ct.ThrowIfCancellationRequested();
            throw AdbErrorMapper.Map(ex, serial, operation);
        }
    }

    private static bool IsUsageError(Exception ex) =>
        ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException;
}
