using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Device.Adb;
using ClickZen.Device.Input;
using Microsoft.Extensions.Logging;

namespace ClickZen.App.Services;

/// <summary>
/// "Record on device": streams <c>getevent</c> from the device's touchscreen (no root needed, the
/// shell user is in the input group) and reports touches in the same coordinate space as the
/// recording page (<see cref="RecordingService.CurrentScreen"/>).
/// </summary>
public sealed class GeteventTouchCapture : IDeviceTouchCapture
{
    private readonly GeteventRecorder _recorder;
    private readonly ILogger<GeteventTouchCapture> _log;

    public GeteventTouchCapture(AdbService adb, ILogger<GeteventTouchCapture> log)
    {
        _recorder = new GeteventRecorder(adb);
        _log = log;
    }

    /// <summary>adb devices only: a window device's touches would need mapping from its linked device.</summary>
    public bool CanCapture(DeviceEntry entry) => !entry.IsWindow && entry.Info.State == DeviceAdbState.Online;

    public async Task CaptureAsync(DeviceEntry entry, Action<RawTouchEvent> onTouch, CancellationToken ct)
    {
        var options = new GeteventRecordingOptions
        {
            NaturalSize = entry.Info.PhysicalSize.IsEmpty ? null : entry.Info.PhysicalSize,
            Density = entry.Info.Density > 0 ? entry.Info.Density : null,
        };
        var session = await _recorder.PrepareAsync(entry.Serial, options, ct);
        _log.LogInformation("Device recording on {Serial}: {Path} ({Name}), screen {W}x{H}, rotation {Rotation}",
            entry.Serial, session.Touchscreen.Path, session.Touchscreen.Name, session.ScreenSize.Width, session.ScreenSize.Height, session.Rotation);

        // The page works in RecordingService.CurrentScreen; rescale if that differs (e.g. adb size override).
        var target = RecordingService.CurrentScreen(entry);
        var source = session.ScreenSize;
        var rescale = !target.IsEmpty && target != source;
        await _recorder.CaptureAsync(entry.Serial, session, e =>
        {
            if (rescale)
            {
                e = e with { Position = new PointD(e.Position.X * target.Width / source.Width, e.Position.Y * target.Height / source.Height) };
            }

            onTouch(e);
        }, ct);
    }
}
