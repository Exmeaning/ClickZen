using ClickZen.App.Controls;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.Foundation;

namespace ClickZen.App.Services;

/// <summary>
/// Developer self-check for live input (<c>--selftest-input</c>): drives the real MirrorView pointer
/// handlers (the same code the mouse uses) without touching the desktop cursor, and verifies on the
/// device that the screen changed. Exit code 0 = pass, 2 = fail, 4 = no device.
/// </summary>
internal sealed class InputSelfTest
{
    private readonly Shell.MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public InputSelfTest(Shell.MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exit = 2;
        try
        {
            var hub = _services.GetRequiredService<DeviceHub>();
            var entry = await WaitForAsync(() => hub.Current is { Session.State: SessionState.Streaming } c ? c : null, TimeSpan.FromSeconds(60));
            if (entry?.Session is not { } session)
            {
                _log.LogError("Self-test: no streaming device");
                exit = 4;
                return;
            }

            _window.NavigateTo("mirror");
            var mirror = await WaitForAsync(() => FindMirror(), TimeSpan.FromSeconds(10));
            var first = await WaitForAsync(() => mirror?.CurrentFrame, TimeSpan.FromSeconds(15));
            if (mirror is null || first is null)
            {
                _log.LogError("Self-test: mirror view never showed a frame");
                return;
            }

            // 1. Go home via the toolbar key path, let the launcher settle.
            await mirror.SendKeyAsync(Core.Input.KeyCodes.Home);
            await Task.Delay(2000);
            var before = await session.WaitForFrameAsync(-1, TimeSpan.FromSeconds(2), CancellationToken.None) ?? first;

            // 2. Swipe up from the lower middle of the picture through the real pointer handlers.
            var f = before.Size;
            var start = mirror.FrameToViewport(new PointD(f.Width / 2.0, f.Height * 0.85));
            var end = mirror.FrameToViewport(new PointD(f.Width / 2.0, f.Height * 0.25));
            if (start is not { } s || end is not { } e)
            {
                _log.LogError("Self-test: could not map frame to viewport");
                return;
            }

            const uint pointer = 9001;
            if (!mirror.HandlePress(s, pointer))
            {
                _log.LogError("Self-test: press was not consumed (input disabled or not mapped)");
                return;
            }

            for (var i = 1; i <= 20; i++)
            {
                var t = i / 20.0;
                mirror.HandleMove(new Point(s.X + (e.X - s.X) * t, s.Y + (e.Y - s.Y) * t), pointer);
                await Task.Delay(15);
            }

            mirror.HandleRelease(e, pointer);
            await Task.Delay(2500);

            // 3. The picture must have changed substantially (app drawer opened).
            var after = await session.WaitForFrameAsync(before.Sequence, TimeSpan.FromSeconds(3), CancellationToken.None) ?? before;
            var diff = MeanDifference(before, after);
            _log.LogInformation("Self-test: frame difference after swipe = {Diff:F1} (sequence {Before} -> {After})", diff, before.Sequence, after.Sequence);

            // Restore the device.
            await mirror.SendKeyAsync(Core.Input.KeyCodes.Home);

            if (diff > 8)
            {
                _log.LogInformation("Input self-test passed");
                exit = 0;
            }
            else
            {
                _log.LogError("Input self-test failed: screen did not change");
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Input self-test crashed");
        }
        finally
        {
            Environment.Exit(exit);
        }
    }

    private MirrorView? FindMirror() =>
        _window.Frame.Content is Views.MirrorPage page ? page.FindName("Mirror") as MirrorView : null;

    private static double MeanDifference(Frame a, Frame b)
    {
        if (a.Size != b.Size)
        {
            return 255;
        }

        long sum = 0;
        var n = 0;
        for (var i = 0; i < a.Bgra.Length; i += 4 * 7)
        {
            sum += Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]);
            n += 3;
        }

        return n == 0 ? 0 : (double)sum / n;
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } v)
            {
                return v;
            }

            await Task.Delay(200);
        }

        return probe();
    }
}
