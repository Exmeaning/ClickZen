using System.Net;
using System.Net.Sockets;
using ClickZen.Device.Adb;
using ClickZen.Device.Scrcpy.Protocol;
using Microsoft.Extensions.Logging;

namespace ClickZen.Device.Scrcpy;

/// <summary>
/// The adb-dependent half of starting scrcpy-server: push the jar, set up the tunnel, run the
/// server process, and produce a connected <see cref="ScrcpyConnection"/>. Abstracted so the
/// session logic can be tested without a device.
/// </summary>
public interface IScrcpyTransport
{
    /// <summary>
    /// Starts the server and returns the connection plus a task that completes when the server
    /// process exits. Disposing the returned handle tears down the tunnel and stops the server.
    /// </summary>
    Task<ScrcpyStartResult> StartAsync(string serial, ScrcpyServerOptions options, CancellationToken ct);
}

public sealed class ScrcpyStartResult : IAsyncDisposable
{
    public ScrcpyStartResult(ScrcpyConnection connection, Task serverExited, Func<ValueTask> cleanup)
    {
        Connection = connection;
        ServerExited = serverExited;
        _cleanup = cleanup;
    }

    private readonly Func<ValueTask> _cleanup;

    public ScrcpyConnection Connection { get; }

    /// <summary>Completes when the device-side server process ends.</summary>
    public Task ServerExited { get; }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _cleanup();
    }
}

/// <summary>Real transport over <see cref="AdbService"/>.</summary>
public sealed class AdbScrcpyTransport : IScrcpyTransport
{
    private readonly AdbService _adb;
    private readonly BundledTools _tools;
    private readonly ILogger? _logger;

    public AdbScrcpyTransport(AdbService adb, BundledTools tools, ILogger<AdbScrcpyTransport>? logger = null)
    {
        _adb = adb;
        _tools = tools;
        _logger = logger;
    }

    /// <summary>How long to wait for the server to connect back / accept connections.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<ScrcpyStartResult> StartAsync(string serial, ScrcpyServerOptions options, CancellationToken ct)
    {
        await _adb.PushAsync(serial, _tools.ScrcpyServerPath, ScrcpyServerInfo.DevicePath, ct: ct);

        // Try reverse first (the device connects to us – no race with server start), then forward.
        try
        {
            return await StartReverseAsync(serial, options with { TunnelForward = false }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.LogInformation(ex, "adb reverse failed for {Serial}, falling back to forward", serial);
        }

        return await StartForwardAsync(serial, options with { TunnelForward = true }, ct);
    }

    private async Task<ScrcpyStartResult> StartReverseAsync(string serial, ScrcpyServerOptions options, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(2);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var remote = "localabstract:" + options.SocketName;
        var serverCts = new CancellationTokenSource();
        Task? server = null;
        try
        {
            await _adb.CreateReverseAsync(serial, remote, $"tcp:{port}", ct: ct);
            server = RunServerAsync(serial, options, serverCts.Token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            var accept = ScrcpyConnection.AcceptAsync(listener, options.Video, timeout.Token);
            var first = await Task.WhenAny(accept, server);
            if (first == server)
            {
                throw new IOException("scrcpy-server exited before connecting.");
            }

            var connection = await accept;
            listener.Stop();
            return new ScrcpyStartResult(connection, server, async () =>
            {
                await serverCts.CancelAsync();
                await IgnoreErrors(_adb.RemoveReverseAsync(serial, remote, CancellationToken.None));
                await IgnoreErrors(server);
                serverCts.Dispose();
            });
        }
        catch
        {
            listener.Stop();
            await serverCts.CancelAsync();
            await IgnoreErrors(_adb.RemoveReverseAsync(serial, remote, CancellationToken.None));
            if (server is not null)
            {
                await IgnoreErrors(server);
            }

            serverCts.Dispose();
            throw;
        }
    }

    private async Task<ScrcpyStartResult> StartForwardAsync(string serial, ScrcpyServerOptions options, CancellationToken ct)
    {
        var serverCts = new CancellationTokenSource();
        var port = 0;
        Task? server = null;
        try
        {
            port = await _adb.CreateForwardAsync(serial, "tcp:0", "localabstract:" + options.SocketName, ct: ct);
            server = RunServerAsync(serial, options, serverCts.Token);
            var connection = await ScrcpyConnection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), options.Video, ConnectTimeout, ct);
            var p = port;
            return new ScrcpyStartResult(connection, server, async () =>
            {
                await serverCts.CancelAsync();
                await IgnoreErrors(_adb.RemoveForwardAsync(serial, p, CancellationToken.None));
                await IgnoreErrors(server);
                serverCts.Dispose();
            });
        }
        catch
        {
            await serverCts.CancelAsync();
            if (port > 0)
            {
                await IgnoreErrors(_adb.RemoveForwardAsync(serial, port, CancellationToken.None));
            }

            if (server is not null)
            {
                await IgnoreErrors(server);
            }

            serverCts.Dispose();
            throw;
        }
    }

    private Task RunServerAsync(string serial, ScrcpyServerOptions options, CancellationToken ct) =>
        Task.Run(async () =>
        {
            try
            {
                await _adb.ExecuteStreamingAsync(serial, options.BuildCommand(), line =>
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logger?.LogDebug("[scrcpy-server {Serial}] {Line}", serial, line);
                    }
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                if (ct.IsCancellationRequested)
                {
                    // Closing the shell normally kills the server; make sure a stuck one goes too.
                    await IgnoreErrors(_adb.ShellAsync(serial, $"pkill -f 'scid={options.Scid:x8}'", TimeSpan.FromSeconds(3), CancellationToken.None));
                }
            }
        }, CancellationToken.None);

    private static async Task IgnoreErrors(Task t)
    {
        try
        {
            await t;
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
    }
}
