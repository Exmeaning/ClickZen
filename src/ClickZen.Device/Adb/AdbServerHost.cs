using System.Net;
using AdvancedSharpAdbClient;
using ClickZen.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClickZen.Device.Adb;

/// <summary>How <see cref="AdbServerHost.EnsureStartedAsync"/> obtained a running server.</summary>
public enum AdbServerStartMode
{
    /// <summary>A compatible server was already listening (started by us earlier, by Android Studio, an emulator…); it was left alone.</summary>
    ReusedExisting,
    /// <summary>No server was running; ours was started.</summary>
    Started,
    /// <summary>An outdated server was running; it was killed and ours was started.</summary>
    RestartedOutdated,
}

/// <summary>Result of <see cref="AdbServerHost.EnsureStartedAsync"/>.</summary>
/// <param name="Mode">What happened.</param>
/// <param name="ServerVersion">adb protocol version of the running server (the "41" in 1.0.41).</param>
/// <param name="ExecutableVersion">Version of <paramref name="AdbPath"/>, or <see langword="null"/> if it could not be queried.</param>
/// <param name="AdbPath">The adb.exe we would use to (re)start the server.</param>
public sealed record AdbServerStatus(AdbServerStartMode Mode, int ServerVersion, int? ExecutableVersion, string AdbPath)
{
    public bool Reused => Mode == AdbServerStartMode.ReusedExisting;
}

/// <summary>
/// Owns the adb server lifecycle. A server that is already running and recent enough is reused instead of
/// restarted, so ClickZen does not get into a kill/restart war with other tools that bundle their own adb
/// (Android Studio, emulators, phone suites…).
/// </summary>
public sealed class AdbServerHost
{
    /// <summary>A running server this many protocol versions older than our adb is still reused.</summary>
    public const int VersionTolerance = 1;

    /// <summary>Default adb server endpoint (127.0.0.1:5037).</summary>
    public static readonly EndPoint DefaultEndPoint = new IPEndPoint(IPAddress.Loopback, 5037);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string?> _customAdbPath;
    private readonly BundledTools _tools;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdbServerStatus? _status;

    public AdbServerHost(SettingsService settings, BundledTools tools, ILogger<AdbServerHost>? logger = null)
        : this(() => settings.Current.Tools.CustomAdbPath, tools, logger, DefaultEndPoint)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    internal AdbServerHost(Func<string?> customAdbPath, BundledTools tools, ILogger? logger, EndPoint endPoint)
    {
        _customAdbPath = customAdbPath;
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _logger = logger ?? NullLogger.Instance;
        EndPoint = endPoint;
    }

    public EndPoint EndPoint { get; }

    /// <summary>Status from the last successful <see cref="EnsureStartedAsync"/>, or <see langword="null"/>.</summary>
    public AdbServerStatus? LastStatus => Volatile.Read(ref _status);

    /// <summary>The adb.exe that would be used: the custom path from settings if it exists, else the bundled one.</summary>
    public string ResolveAdbPath() => ResolveAdbPath(_customAdbPath(), _tools.AdbPath);

    internal static string ResolveAdbPath(string? customPath, string bundledPath)
    {
        var custom = customPath?.Trim().Trim('"');
        return !string.IsNullOrEmpty(custom) && File.Exists(custom) ? custom : bundledPath;
    }

    /// <summary>Queries the running server's protocol version; <see langword="null"/> if no server is listening.</summary>
    public Task<int?> GetRunningServerVersionAsync(CancellationToken ct = default) =>
        Task.Run(() => ProbeServerVersionAsync(ct), ct);

    /// <summary>
    /// Makes sure an adb server is listening on <see cref="EndPoint"/>. Safe to call repeatedly and concurrently.
    /// </summary>
    /// <exception cref="AdbException"><see cref="AdbErrorKind.ServerStartFailed"/> when no usable server could be obtained.</exception>
    public async Task<AdbServerStatus> EnsureStartedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var status = await Task.Run(() => EnsureStartedCoreAsync(ct), ct);
            Volatile.Write(ref _status, status);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AdbServerStatus> EnsureStartedCoreAsync(CancellationToken ct)
    {
        var adbPath = ResolveAdbPath();
        var custom = _customAdbPath();
        if (!string.IsNullOrWhiteSpace(custom) && !string.Equals(adbPath, custom.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Custom adb path {CustomPath} does not exist; falling back to {AdbPath}", custom, adbPath);
        }

        var running = await ProbeServerVersionAsync(ct);
        var exeVersion = await GetExecutableVersionAsync(adbPath, ct);
        var decision = Decide(running, exeVersion);
        _logger.LogInformation(
            "adb server probe: running={RunningVersion}, executable={ExecutableVersion} ({AdbPath}) -> {Decision}",
            running, exeVersion, adbPath, decision);

        switch (decision)
        {
            case AdbServerDecision.Reuse:
                return new AdbServerStatus(AdbServerStartMode.ReusedExisting, running!.Value, exeVersion, adbPath);

            case AdbServerDecision.Fail:
                throw new AdbException(
                    AdbErrorKind.ServerStartFailed,
                    running is null
                        ? $"No adb server is running and the adb executable '{adbPath}' is missing or unusable."
                        : $"The running adb server (version {running}) is too old and '{adbPath}' cannot replace it.");

            default:
                {
                    var restart = decision == AdbServerDecision.Restart;
                    await StartAsync(adbPath, restart, ct);
                    var after = await WaitForServerAsync(ct)
                        ?? throw new AdbException(AdbErrorKind.ServerStartFailed, $"adb server did not come up after starting '{adbPath}'.");
                    return new AdbServerStatus(restart ? AdbServerStartMode.RestartedOutdated : AdbServerStartMode.Started, after, exeVersion, adbPath);
                }
        }
    }

    internal enum AdbServerDecision
    {
        Reuse,
        Start,
        Restart,
        Fail,
    }

    /// <summary>Pure decision table – see class remarks.</summary>
    internal static AdbServerDecision Decide(int? runningVersion, int? executableVersion)
    {
        var required = AdbServer.RequiredAdbVersion.Build;
        if (runningVersion is not int running)
        {
            return executableVersion is null ? AdbServerDecision.Fail : AdbServerDecision.Start;
        }

        if (executableVersion is not int exe)
        {
            // We cannot start anything ourselves; use whatever is there if the library can talk to it.
            return running >= required ? AdbServerDecision.Reuse : AdbServerDecision.Fail;
        }

        if (running >= required && running >= exe - VersionTolerance)
        {
            return AdbServerDecision.Reuse;
        }

        return exe > running ? AdbServerDecision.Restart : AdbServerDecision.Fail;
    }

    private async Task<int?> ProbeServerVersionAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var client = new AdbClient(EndPoint);
            return await client.GetAdbVersionAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("adb server on {EndPoint} did not answer within {Timeout}", EndPoint, ProbeTimeout);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var kind = AdbErrorMapper.Classify(ex);
            if (kind != AdbErrorKind.ConnectionRefused)
            {
                _logger.LogDebug(ex, "adb server probe failed");
            }

            return null;
        }
    }

    private async Task<int?> GetExecutableVersionAsync(string adbPath, CancellationToken ct)
    {
        if (!File.Exists(adbPath))
        {
            _logger.LogWarning("adb executable not found at {AdbPath}", adbPath);
            return null;
        }

        try
        {
            var cli = new AdbCommandLineClient(adbPath, false, null);
            var status = await cli.GetVersionAsync(ct);
            return status.AdbVersion?.Build;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not query the version of {AdbPath}", adbPath);
            return null;
        }
    }

    private async Task StartAsync(string adbPath, bool restartIfNewer, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        try
        {
            var server = new AdbServer(new AdbClient(EndPoint));
            var result = await server.StartServerAsync(adbPath, restartIfNewer, timeout.Token);
            _logger.LogInformation("adb StartServer({AdbPath}, restartIfNewer: {Restart}) -> {Result}", adbPath, restartIfNewer, result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdbException(AdbErrorKind.ServerStartFailed, $"Starting the adb server with '{adbPath}' timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AdbException)
        {
            throw new AdbException(AdbErrorKind.ServerStartFailed, $"Failed to start the adb server with '{adbPath}': {ex.Message}", null, ex);
        }
    }

    private async Task<int?> WaitForServerAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await ProbeServerVersionAsync(ct) is int v)
            {
                return v;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        }

        return null;
    }
}
