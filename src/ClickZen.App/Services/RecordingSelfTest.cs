using ClickZen.App.Controls;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Core.Recording;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.Foundation;

namespace ClickZen.App.Services;

/// <summary>
/// Developer self-check for recording (<c>--selftest-recording</c>): on the recording page, records a swipe
/// made through the real MirrorView pointer handlers, saves and reloads it as .czrec, then replays it through
/// <see cref="RecordingService"/> and verifies on the device that the screen changed.
/// Exit code 0 = pass, 2 = fail, 4 = no device.
/// </summary>
internal sealed class RecordingSelfTest
{
    private readonly Shell.MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public RecordingSelfTest(Shell.MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exit = 2;
        string? file = null;
        try
        {
            var hub = _services.GetRequiredService<DeviceHub>();
            var entry = await SelfTestUtil.WaitForAsync(() => hub.Current is { Session.State: SessionState.Streaming } c ? c : null, TimeSpan.FromSeconds(60));
            if (entry?.Session is not { } session)
            {
                _log.LogError("Recording self-test: no streaming device");
                exit = 4;
                return;
            }

            _window.NavigateTo("recording");
            var page = await SelfTestUtil.WaitForAsync(() => _window.Frame.Content as Views.RecordingPage, TimeSpan.FromSeconds(10));
            var mirror = page?.FindName("Mirror") as MirrorView;
            var first = await SelfTestUtil.WaitForAsync(() => mirror?.CurrentFrame, TimeSpan.FromSeconds(15));
            if (page is null || mirror is null || first is null)
            {
                _log.LogError("Recording self-test: recording page never showed a frame");
                return;
            }

            var rec = page.Recorder;
            rec.New();
            await mirror.SendKeyAsync(Core.Input.KeyCodes.Home);
            await Task.Delay(2000);

            // 1. Record a swipe up (opens the app drawer) through the real pointer path.
            rec.StartRecording(entry, RecordingService.CurrentScreen(entry), RecordingInputSource.Mirror);
            var f = first.Size;
            if (mirror.FrameToViewport(new PointD(f.Width / 2.0, f.Height * 0.85)) is not { } s
                || mirror.FrameToViewport(new PointD(f.Width / 2.0, f.Height * 0.25)) is not { } e)
            {
                _log.LogError("Recording self-test: could not map frame to viewport");
                return;
            }

            const uint pointer = 9002;
            mirror.HandlePress(s, pointer);
            for (var i = 1; i <= 20; i++)
            {
                var t = i / 20.0;
                mirror.HandleMove(new Point(s.X + (e.X - s.X) * t, s.Y + (e.Y - s.Y) * t), pointer);
                await Task.Delay(15);
            }

            mirror.HandleRelease(e, pointer);
            rec.StopRecording();
            var recorded = rec.Document.Gestures;
            _log.LogInformation("Recording self-test: recorded {Count} gesture(s): {Kinds}", recorded.Count, string.Join(",", recorded.Select(g => g.Kind)));
            if (recorded.Count != 1 || recorded[0].Kind != GestureKind.Swipe || recorded[0].Fingers[0].Points.Count < 5)
            {
                _log.LogError("Recording self-test failed: expected one swipe with a full trajectory");
                return;
            }

            // 2. Save and reload as .czrec.
            file = Path.Combine(Path.GetTempPath(), $"cz-selftest-{Guid.NewGuid():N}{RecordingDocument.FileExtension}");
            rec.Save(file);
            rec.New();
            rec.Open(file);
            if (rec.Document.Gestures.Count != 1 || rec.Items.Count != 1)
            {
                _log.LogError("Recording self-test failed: .czrec round trip lost gestures");
                return;
            }

            // 3. Back home, then replay and check that the drawer opened again.
            await mirror.SendKeyAsync(Core.Input.KeyCodes.Home);
            await Task.Delay(2000);
            var before = await session.WaitForFrameAsync(-1, TimeSpan.FromSeconds(2), CancellationToken.None) ?? first;
            rec.Speed = 1;
            rec.Loops = 1;
            rec.Randomize = false;
            var outcome = await rec.PlayAsync(entry, 0);
            await Task.Delay(2000);
            var after = await session.WaitForFrameAsync(before.Sequence, TimeSpan.FromSeconds(3), CancellationToken.None) ?? before;
            var diff = SelfTestUtil.MeanDifference(before, after);
            _log.LogInformation("Recording self-test: playback {Outcome}, frame difference {Diff:F1}", outcome, diff);
            await mirror.SendKeyAsync(Core.Input.KeyCodes.Home);

            if (outcome == PlaybackOutcome.Completed && diff > 8)
            {
                _log.LogInformation("Recording self-test passed");
                exit = 0;
            }
            else
            {
                _log.LogError("Recording self-test failed: playback did not change the screen");
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Recording self-test crashed");
        }
        finally
        {
            if (file is not null)
            {
                File.Delete(file);
            }

            Environment.Exit(exit);
        }
    }
}

/// <summary>Helpers shared by the developer self-tests.</summary>
internal static class SelfTestUtil
{
    public static double MeanDifference(Frame a, Frame b)
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

    public static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
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
