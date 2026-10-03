using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;

namespace ClickZen.Device.Tests.Adb;

/// <summary>
/// Scriptable <see cref="IAdbClient"/> built with <see cref="DispatchProxy"/> so tests need neither adb nor a device.
/// Only the members used by AdbService are implemented; anything else throws <see cref="NotSupportedException"/>.
/// </summary>
public class FakeAdbClient : DispatchProxy
{
    public EndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 5037);

    /// <summary>Devices returned by GetDevicesAsync.</summary>
    public List<DeviceData> Devices { get; } = [];

    /// <summary>Shell handler: (serial, command, ct) → output lines. Default returns nothing.</summary>
    public Func<string, string, CancellationToken, Task<IEnumerable<string>>> Shell { get; set; } =
        (_, _, _) => Task.FromResult(Enumerable.Empty<string>());

    /// <summary>Connect handler: (address, ct) → reply.</summary>
    public Func<string, CancellationToken, Task<string>> Connect { get; set; } =
        (a, _) => Task.FromResult($"failed to connect to '{a}': Connection refused");

    public Func<string, int, string, CancellationToken, Task<string>> Pair { get; set; } =
        (_, _, _, _) => Task.FromResult("Failed: Wrong password or connection was dropped.");

    public ConcurrentQueue<string> ShellLog { get; } = new();

    public ConcurrentQueue<string> ConnectLog { get; } = new();

    public static (IAdbClient Client, FakeAdbClient Fake) Create()
    {
        var proxy = Create<IAdbClient, FakeAdbClient>();
        return (proxy, (FakeAdbClient)(object)proxy);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        switch (targetMethod.Name)
        {
            case "get_EndPoint":
                return EndPoint;
            case nameof(IAdbClient.GetDevicesAsync):
                return Task.FromResult<IEnumerable<DeviceData>>(Devices.ToArray());
            case nameof(IAdbClient.ExecuteRemoteCommandAsync) when args.Length == 5:
                return RunShellAsync((string)args[0]!, (DeviceData)args[1]!, (IShellOutputReceiver?)args[2], (CancellationToken)args[4]!);
            case nameof(IAdbClient.ConnectAsync):
                {
                    var address = (string)args[0]!;
                    ConnectLog.Enqueue(address);
                    return Connect(address, (CancellationToken)args[2]!);
                }

            case nameof(IAdbClient.PairAsync):
                return Pair((string)args[0]!, (int)args[1]!, (string)args[2]!, (CancellationToken)args[3]!);
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }

    private async Task RunShellAsync(string command, DeviceData device, IShellOutputReceiver? receiver, CancellationToken ct)
    {
        ShellLog.Enqueue($"{device.Serial}: {command}");
        foreach (var line in await Shell(device.Serial, command, ct))
        {
            if (receiver is not null && !await receiver.AddOutputAsync(line, ct))
            {
                break;
            }
        }
    }
}
