using ClickZen.App.Controls;
using ClickZen.Core.Automation;
using ClickZen.Core.Devices;
using ClickZen.Core.Geometry;
using ClickZen.Device.Scrcpy;
using ClickZen.Platform.Capture;
using ClickZen.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClickZen.App.Services;

/// <summary>
/// Developer self-check for M7 (<c>--selftest-window</c>): finds the Android Emulator (AVD) window, creates a
/// temporary emulator profile programmatically (whole client area, reference = the linked adb device's screen,
/// input = that device), lets it appear as a window device and captures frames from it; then, on the automation
/// page with the window device as current device, cuts the home-screen dock's first icon from the window frame
/// as a template and runs "When image found → Tap LastMatch", expecting the tap (through the linked adb device)
/// to open the app. Finally goes home and deletes the profile.
/// Run with CLICKZEN_DATA_DIR pointing to a temporary directory. Exit code 0 = pass, 2 = fail, 4 = no AVD window / adb device.
/// </summary>
internal sealed class WindowSelfTest
{
    private const string AdbSerial = "emulator-5554";

    private readonly Shell.MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public WindowSelfTest(Shell.MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exit = 2;
        EmulatorProfile? profile = null;
        DeviceHub? hub = null;
        AutomationService? service = null;
        Core.Input.ITouchInjector? injector = null;
        try
        {
            hub = _services.GetRequiredService<DeviceHub>();

            // 1. The AVD window and its adb device.
            var rule = new WindowMatchRule { ProcessName = "qemu-system-x86_64", Title = "Android Emulator", TitleMode = TitleMatchMode.Contains };
            var window = WindowMatcher.FindBestMatch(rule, WindowEnumerator.GetVisibleWindows((uint)Environment.ProcessId));
            var adbEntry = await SelfTestUtil.WaitForAsync(
                () => hub.Devices.FirstOrDefault(d => !d.IsWindow && d.Serial == AdbSerial && d.Info.State == DeviceAdbState.Online && !d.Info.PhysicalSize.IsEmpty),
                TimeSpan.FromSeconds(30));
            if (window is null || adbEntry is null)
            {
                _log.LogError("Window self-test: no AVD window ({Window}) or no online {Serial} ({Adb})", window?.Title ?? "none", AdbSerial, adbEntry is not null);
                exit = 4;
                return;
            }

            if (!WindowMetrics.TryQuery(window.Handle, out var metrics))
            {
                _log.LogError("Window self-test: cannot query the AVD window");
                return;
            }

            // 2. Look at the client area to decide the crop: the AVD (no-skin) window shows only the Android screen,
            //    so the whole client area is the screen; confirm by comparing the aspect ratio with the device.
            var probe = await WindowFrameSource.CaptureOnceAsync(window.Handle, null, WindowCaptureMethod.Auto, TimeSpan.FromSeconds(5));
            var physical = adbEntry.Info.PhysicalSize;
            if (probe is null)
            {
                _log.LogError("Window self-test: CaptureOnceAsync returned nothing for {Hwnd:X}", window.Handle);
                return;
            }

            await DumpAsync("window-client.png", probe);
            var reference = physical.IsLandscape == probe.Size.IsLandscape ? physical : physical.Transposed;
            var crop = AndroidArea(probe.Size, reference);
            _log.LogInformation("Window self-test: window \"{Title}\" ({Process}) client {Client}, device {Device}, crop {Crop}, reference {Ref}",
                window.Title, window.ProcessName, metrics.ClientSize, physical, crop?.ToString() ?? "client area", reference);

            // 3. Create the profile through the store: it must show up as a window device and start capturing.
            profile = new EmulatorProfile
            {
                Name = "Self-test AVD",
                Match = WindowMatcher.SuggestRule(window),
                CropRect = crop,
                ClientSize = metrics.ClientSize,
                ReferenceSize = reference,
                AdbSerial = AdbSerial,
            };
            hub.Profiles.Upsert(profile);
            var entry = await SelfTestUtil.WaitForAsync(() => hub.FindWindowDevice(profile.Id), TimeSpan.FromSeconds(5));
            if (entry is null)
            {
                _log.LogError("Window self-test failed: the profile did not appear as a device");
                return;
            }

            if (entry.Frames is null)
            {
                await hub.ConnectAsync(entry);
            }

            var source = await SelfTestUtil.WaitForAsync(() => entry.WindowSource, TimeSpan.FromSeconds(10));
            var first = source is null ? null : await source.WaitForFrameAsync(-1, TimeSpan.FromSeconds(5), CancellationToken.None);
            if (source is null || first is null || entry.SessionState != SessionState.Streaming)
            {
                _log.LogError("Window self-test failed: window device state {State} ({Error}), frame {Frame}", entry.SessionState, entry.SessionError, first?.Size);
                return;
            }

            var expectedFrame = crop?.Size ?? metrics.ClientSize;
            _log.LogInformation("Window self-test: window device {Serial} streaming via {Method}, frame {Frame} #{Seq}, screen {Screen}, adb {Adb}",
                entry.Serial, source.ActiveMethod, first.Size, first.Sequence, entry.ScreenSize, entry.AdbSerial);
            if (first.Size != expectedFrame || entry.ScreenSize != reference || BgraBuffer.IsLikelyBlack(first.Bgra, first.Width, first.Height))
            {
                _log.LogError("Window self-test failed: frame {Frame} (expected {Expected}), screen {Screen} (expected {Ref}) or black picture",
                    first.Size, expectedFrame, entry.ScreenSize, reference);
                return;
            }

            // 4. Input goes through the linked adb device, scaled from the reference resolution.
            injector = await hub.GetInjectorAsync(entry);
            if (injector is not ScaledTouchInjector)
            {
                _log.LogError("Window self-test failed: window device input is {Input}, expected the linked adb device", injector.Name);
                return;
            }

            // 4b. The binding wizard, opened on the profile (edit mode), walks through all steps with the live
            //     thumbnail and the prefilled values; it is closed without saving.
            if (!await WalkWizardAsync(hub, profile))
            {
                return;
            }

            // 4c. The mirror page shows the window picture and forwards input (Home key) to the linked device.
            hub.Current = entry;
            _window.NavigateTo("mirror");
            var mirror = await SelfTestUtil.WaitForAsync(
                () => _window.Frame.Content is Views.MirrorPage mp ? mp.FindName("Mirror") as MirrorView : null, TimeSpan.FromSeconds(10));
            var shownOnMirror = mirror is null ? null : await SelfTestUtil.WaitForAsync(() => mirror.CurrentFrame, TimeSpan.FromSeconds(10));
            var mirrorFailed = (string?)null;
            if (mirror is not null)
            {
                mirror.InputFailed += (_, m) => mirrorFailed = m;
                await mirror.SendKeyAsync(Core.Input.KeyCodes.Home);
                await Task.Delay(500);
            }

            _log.LogInformation("Window self-test: mirror page source is window capture: {Same}, frame {Frame}, input error {Error}",
                ReferenceEquals(mirror?.Source, source), shownOnMirror?.Size, mirrorFailed ?? "none");
            if (mirror is null || !ReferenceEquals(mirror.Source, source) || shownOnMirror?.Size != expectedFrame || mirrorFailed is not null)
            {
                _log.LogError("Window self-test failed: the mirror page did not show or control the window device");
                return;
            }

            // 4d. A swipe up on the mirror view (window picture) opens the app drawer on the emulator.
            await Task.Delay(1500);
            var homeFrame = source.Latest!;
            var fs = homeFrame.Size;
            if (mirror.FrameToViewport(new PointD(fs.Width / 2.0, fs.Height * 0.85)) is not { } vs
                || mirror.FrameToViewport(new PointD(fs.Width / 2.0, fs.Height * 0.3)) is not { } ve
                || !mirror.HandlePress(vs, 9101))
            {
                _log.LogError("Window self-test failed: the mirror view did not accept a press on the window picture");
                return;
            }

            for (var i = 1; i <= 15; i++)
            {
                var t = i / 15.0;
                mirror.HandleMove(new Windows.Foundation.Point(vs.X + (ve.X - vs.X) * t, vs.Y + (ve.Y - vs.Y) * t), 9101);
                await Task.Delay(15);
            }

            mirror.HandleRelease(ve, 9101);
            var swipeDiff = 0.0;
            for (var i = 0; i < 12 && swipeDiff <= 8; i++)
            {
                await Task.Delay(250);
                swipeDiff = SelfTestUtil.MeanDifference(homeFrame, source.Latest ?? homeFrame);
            }

            _log.LogInformation("Window self-test: mirror swipe on the window picture changed it by {Diff:F1} (input error {Error})", swipeDiff, mirrorFailed ?? "none");
            await mirror.SendKeyAsync(Core.Input.KeyCodes.Home);
            if (swipeDiff <= 8 || mirrorFailed is not null)
            {
                _log.LogError("Window self-test failed: the swipe on the mirror view did not reach the emulator");
                return;
            }

            hub.Current = entry;
            await AutomationSelfTest.GoHomeAsync(injector);

            // 5. Automation page on the window device.
            _window.NavigateTo("automation");
            var page = await SelfTestUtil.WaitForAsync(() => _window.Frame.Content as Views.AutomationPage, TimeSpan.FromSeconds(10));
            var bench = page?.Workbench;
            if (page is null || bench is null || await SelfTestUtil.WaitForAsync(() => bench.CurrentFrame, TimeSpan.FromSeconds(15)) is null)
            {
                _log.LogError("Window self-test failed: the automation page never showed the window picture");
                return;
            }

            service = page.Service;
            var host = (IAutomationEditorHost)page;
            service.New();
            var screen = entry.ScreenSize;

            // Template through the real workbench drag, on a fresh home-screen frame.
            await Task.Delay(500);
            bench.Refresh();
            bench.SetFrozen(true);
            await Task.Delay(300);
            var frame = bench.CurrentFrame!;
            await DumpAsync("window-home.png", frame);
            var iconCenter = AutomationSelfTest.FindIcon(frame);
            var half = frame.Width * 0.065;
            var a = new PointD(iconCenter.X - half, iconCenter.Y - half);
            var b = new PointD(iconCenter.X + half, iconCenter.Y + half);
            page.AutoTemplateName = "WindowIcon";
            var capture = host.CaptureTemplateAsync();
            await Task.Delay(100);
            if (!await AutomationSelfTest.DragAsync(bench, a, b) || await capture is not { } captured)
            {
                _log.LogError("Window self-test failed: template capture on the window picture did not work");
                return;
            }

            var (template, area) = captured;
            _log.LogInformation("Window self-test: template {Size} cut at frame ({X:F0}, {Y:F0}) = device area {Area}, scheme RefSize {Ref}",
                template.Size, iconCenter.X, iconCenter.Y, area, service.Scheme.RefSize);
            if (service.Scheme.RefSize != screen)
            {
                _log.LogError("Window self-test failed: scheme RefSize {Ref} is not the window device screen {Screen}", service.Scheme.RefSize, screen);
                return;
            }

            var searchArea = new RectI(0, Math.Max(0, area.Y - area.Height), screen.Width, Math.Min(screen.Height - Math.Max(0, area.Y - area.Height), area.Height * 3));
            var condition = new ImageCondition { TemplateId = template.Id, Area = searchArea, Threshold = 0.8 };
            service.AddTask(new AutomationTask
            {
                Name = "Open app (window)",
                CooldownMs = 5000,
                RunLimit = 1,
                Rules =
                [
                    new Rule
                    {
                        Name = "icon",
                        When = new ConditionGroup { Conditions = [condition] },
                        Then = [new TapAction { Target = new Target { Kind = TargetKind.LastMatch } }],
                    },
                ],
            });
            service.CheckIntervalMs = 200;
            service.Scheme.Settings.Humanize = Core.Input.HumanizeOptions.None;

            var test = await host.TestConditionAsync(condition);
            _log.LogInformation("Window self-test: live test matched={Matched} score={Score:F3} at {Loc}", test.Matched, test.Score, test.Location);
            if (!test.Matched)
            {
                _log.LogError("Window self-test failed: the template was not found on the window picture");
                return;
            }

            // 6. Run on the window device; the tap must open the app.
            bench.SetFrozen(false);
            await AutomationSelfTest.GoHomeAsync(injector);
            var before = await source.WaitForFrameAsync(-1, TimeSpan.FromMilliseconds(500), CancellationToken.None) ?? frame;
            var homeActivity = await AutomationSelfTest.ResumedActivityAsync(hub, AdbSerial);
            _log.LogInformation("Window self-test: resumed activity before run: {Activity}", homeActivity);
            var fired = new TaskCompletionSource<TaskFiredInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFired(object? s, TaskFiredInfo e) => fired.TrySetResult(e);
            service.TaskFired += OnFired;
            service.TargetSerials.Clear();
            await page.ToggleRunAsync();
            if (!service.IsRunning || service.Runs.FirstOrDefault()?.Entry != entry)
            {
                _log.LogError("Window self-test failed: the scheme did not start on the window device");
                return;
            }

            var winner = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            service.TaskFired -= OnFired;
            if (winner != fired.Task)
            {
                _log.LogError("Window self-test failed: the task never fired");
                return;
            }

            var info = await fired.Task;
            _log.LogInformation("Window self-test: task fired on {Device}: score {Score:F3} at device {At}",
                info.Run.Label, info.Args.Match?.Score, info.Args.Match?.Location);

            var diff = 0.0;
            var after = before;
            string? resumed = null;
            var leftHome = false;
            for (var i = 0; i < 60 && diff <= 8 && !leftHome; i++)
            {
                await Task.Delay(500);
                after = source.Latest ?? after;
                diff = SelfTestUtil.MeanDifference(before, after);
                if (i % 2 == 1)
                {
                    resumed = await AutomationSelfTest.ResumedActivityAsync(hub, AdbSerial);
                    leftHome = resumed is not null && homeActivity is not null && resumed != homeActivity;
                }
            }

            await DumpAsync("window-after.png", after);
            await page.ToggleRunAsync();
            foreach (var line in service.Log.TakeLast(8))
            {
                _log.LogInformation("Window self-test log: {Line}", line.ToString());
            }

            _log.LogInformation("Window self-test: frame #{Before} -> #{After}, difference {Diff:F1}, resumed {Resumed}, window fps {Fps:F1}",
                before.Sequence, after.Sequence, diff, resumed, source.Fps);
            if (diff > 8 || leftHome)
            {
                _log.LogInformation("Window self-test passed");
                exit = 0;
            }
            else
            {
                _log.LogError("Window self-test failed: the tap did not open the app (check `adb shell input tap` works on the emulator)");
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Window self-test crashed");
        }
        finally
        {
            try
            {
                if (service is not null)
                {
                    if (service.IsRunning)
                    {
                        await service.StopAsync();
                    }

                    service.New();
                }

                if (injector is ScaledTouchInjector)
                {
                    await injector.KeyAsync(Core.Input.KeyCodes.Home, CancellationToken.None);
                }

                if (hub is not null && profile is not null)
                {
                    hub.Profiles.Remove(profile.Id);
                    await Task.Delay(300);
                    _log.LogInformation("Window self-test: temporary profile removed, window device present: {Present}", hub.FindWindowDevice(profile.Id) is not null);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Window self-test cleanup failed");
            }

            Environment.Exit(exit);
        }
    }

    private async Task<bool> WalkWizardAsync(DeviceHub hub, EmulatorProfile profile)
    {
        _window.NavigateTo("devices");
        await Task.Delay(300);
        var dialog = new Views.BindWindowDialog(hub, profile) { XamlRoot = _window.Content.XamlRoot };
        var shown = dialog.ShowAsync();
        try
        {
            if (await SelfTestUtil.WaitForAsync(() => dialog.HasPreview ? dialog : null, TimeSpan.FromSeconds(10)) is null)
            {
                _log.LogError("Window self-test failed: the wizard's live thumbnail never showed the window");
                return false;
            }

            for (var step = 0; step < 3; step++)
            {
                await Task.Delay(300);
                if (dialog.TryAdvance() is { } problem)
                {
                    _log.LogError("Window self-test failed: wizard step {Step} refused to advance: {Problem}", dialog.Step, problem);
                    return false;
                }
            }

            var p = dialog.PreviewProfile;
            _log.LogInformation("Window self-test: wizard reached step {Step}: name {Name}, crop {Crop}, reference {Ref}, adb {Adb}, rule {Title}|{Process}|{Class}",
                dialog.Step, p.Name, p.CropRect?.ToString() ?? "client area", p.ReferenceSize, p.AdbSerial, p.Match.Title, p.Match.ProcessName, p.Match.ClassName);
            if (dialog.Step != 3 || p.Id != profile.Id || p.ReferenceSize != profile.ReferenceSize || p.AdbSerial != profile.AdbSerial
                || p.CropRect != profile.CropRect || !WindowMatcher.IsMatch(p.Match, WindowEnumerator.GetVisibleWindows().First(w => WindowMatcher.IsMatch(profile.Match, w))))
            {
                _log.LogError("Window self-test failed: the wizard did not keep the profile's values");
                return false;
            }

            return true;
        }
        finally
        {
            dialog.Hide();
            await shown;
            if (dialog.SavedProfile is not null)
            {
                _log.LogWarning("Window self-test: the wizard saved unexpectedly");
            }
        }
    }

    /// <summary>
    /// The Android screen inside the client area: null (whole client area) when the client area already has the
    /// device's aspect ratio; otherwise the largest centred rectangle with that aspect ratio (letterboxing/toolbars).
    /// </summary>
    internal static RectI? AndroidArea(SizeI client, SizeI reference)
    {
        if (client.IsEmpty || reference.IsEmpty)
        {
            return null;
        }

        var ca = (double)client.Width / client.Height;
        var ra = (double)reference.Width / reference.Height;
        if (Math.Abs(ca - ra) / ra < 0.02)
        {
            return null;
        }

        return ca > ra
            ? new RectI((client.Width - (int)Math.Round(client.Height * ra)) / 2, 0, (int)Math.Round(client.Height * ra), client.Height)
            : new RectI(0, (client.Height - (int)Math.Round(client.Width / ra)) / 2, client.Width, (int)Math.Round(client.Width / ra));
    }

    private static async Task DumpAsync(string name, Frame frame)
    {
        if (Environment.GetEnvironmentVariable("CLICKZEN_SELFTEST_DUMP") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, name), Vision.ImageCodec.ToPng(frame));
        }
    }
}
