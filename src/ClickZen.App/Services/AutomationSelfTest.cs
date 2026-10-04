using ClickZen.App.Controls;
using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Device.Scrcpy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.Foundation;

namespace ClickZen.App.Services;

/// <summary>
/// Developer self-check for M6 (<c>--selftest-automation</c>), on the automation page with the current streaming device:
/// home screen → new scheme → capture a template of a home-screen app icon through the real workbench drag →
/// task "When image found → Tap LastMatch" → test the condition → save as .czscheme and reopen →
/// run → expect TaskFired and a screen change (the app opened) → stop → clean up.
/// Exit code 0 = pass, 2 = fail, 4 = no device.
/// </summary>
internal sealed class AutomationSelfTest
{
    private readonly Shell.MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    public AutomationSelfTest(Shell.MainWindow window, IServiceProvider services, ILogger log)
    {
        _window = window;
        _services = services;
        _log = log;
    }

    public async Task RunAsync()
    {
        var exit = 2;
        string? file = null;
        AutomationService? service = null;
        Views.AutomationPage? page = null;
        try
        {
            var hub = _services.GetRequiredService<DeviceHub>();
            var entry = await SelfTestUtil.WaitForAsync(() => hub.Current is { Session.State: SessionState.Streaming } c ? c : null, TimeSpan.FromSeconds(60));
            if (entry?.Session is not { } session)
            {
                _log.LogError("Automation self-test: no streaming device");
                exit = 4;
                return;
            }

            _window.NavigateTo("automation");
            page = await SelfTestUtil.WaitForAsync(() => _window.Frame.Content as Views.AutomationPage, TimeSpan.FromSeconds(10));
            var bench = page?.Workbench;
            if (page is null || bench is null || await SelfTestUtil.WaitForAsync(() => bench.CurrentFrame, TimeSpan.FromSeconds(15)) is null)
            {
                _log.LogError("Automation self-test: automation page never showed a frame");
                return;
            }

            service = page.Service;
            var host = (IAutomationEditorHost)page;
            var injector = await hub.GetInjectorAsync(entry);

            // 1. Home screen, new scheme.
            await GoHomeAsync(injector);
            service.New();
            var screen = RecordingService.CurrentScreen(entry);
            _log.LogInformation("Automation self-test: device {Device}, screen {Screen}, video {Video}, input {Input}",
                entry.Serial, screen, session.VideoSize, injector.Name);

            // 2. Capture a template through the workbench drag (the dock row of icons, near the bottom).
            bench.SetFrozen(true);
            await Task.Delay(300);
            var frame = bench.CurrentFrame!;
            var iconCenter = FindIcon(frame);
            var half = frame.Width * 0.065;
            var a = new PointD(iconCenter.X - half, iconCenter.Y - half);
            var b = new PointD(iconCenter.X + half, iconCenter.Y + half);
            _log.LogInformation("Automation self-test: template around frame point ({X:F0}, {Y:F0})", iconCenter.X, iconCenter.Y);

            page.AutoTemplateName = "SelfTestIcon";
            var capture = host.CaptureTemplateAsync();
            await Task.Delay(100);
            if (!await DragAsync(bench, a, b))
            {
                _log.LogError("Automation self-test: workbench did not accept the drag");
                return;
            }

            if (await capture is not { } captured)
            {
                _log.LogError("Automation self-test failed: template capture returned nothing");
                return;
            }

            var (template, area) = captured;
            var pngSize = Vision.ImageCodec.MeasurePng(template.Png);
            _log.LogInformation("Automation self-test: template {Name} {Size} (png {Png}), area {Area}, RefSize {Ref}",
                template.Name, template.Size, pngSize, area, service.Scheme.RefSize);
            if (service.Scheme.RefSize != screen || pngSize != template.Size || template.Size != area.Size || service.Scheme.Templates.Count != 1)
            {
                _log.LogError("Automation self-test failed: template size / RefSize mismatch");
                return;
            }

            // 3. Task: when the icon is visible (searched in a band around it) → tap it.
            var searchArea = new RectI(0, Math.Max(0, area.Y - area.Height), screen.Width, Math.Min(screen.Height - Math.Max(0, area.Y - area.Height), area.Height * 3));
            var condition = new ImageCondition { TemplateId = template.Id, Area = searchArea, Threshold = 0.8 };
            var task = new AutomationTask
            {
                Name = "Open app",
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
            };
            service.AddTask(task);
            service.CheckIntervalMs = 200;
            service.Scheme.Settings.Humanize = Core.Input.HumanizeOptions.None;

            // 4. Live test through the host API (same maths as the engine).
            var test = await host.TestConditionAsync(condition);
            _log.LogInformation("Automation self-test: live test matched={Matched} score={Score:F3} at {Loc}", test.Matched, test.Score, test.Location);
            if (!test.Matched || test.Location is not { } loc || Math.Abs(loc.X - area.X) > 6 || Math.Abs(loc.Y - area.Y) > 6)
            {
                _log.LogError("Automation self-test failed: the live test did not find the template where it was cut");
                return;
            }

            // 5. Save as .czscheme and reopen.
            file = Path.Combine(Path.GetTempPath(), $"cz-selftest-{Guid.NewGuid():N}{SchemePackage.FileExtension}");
            service.Rename("Self-test");
            service.Save(file);
            service.New();
            service.Open(file);
            var reopened = service.Scheme;
            if (reopened.Tasks.Count != 1 || reopened.Templates.Count != 1 || reopened.Templates[0].Png.Length == 0
                || reopened.RefSize != screen || reopened.Tasks[0].Rules[0].When.Conditions[0] is not ImageCondition rc || rc.TemplateId != template.Id
                || service.IsDirty || service.Validate().Count != 0)
            {
                _log.LogError("Automation self-test failed: .czscheme round trip lost data");
                return;
            }

            _log.LogInformation("Automation self-test: saved and reopened {File} ({Bytes} bytes)", file, new FileInfo(file).Length);

            // 6. Run on the current device: the task must fire and the tap must open the app.
            bench.SetFrozen(false);
            await GoHomeAsync(injector);
            var before = await session.WaitForFrameAsync(-1, TimeSpan.FromSeconds(2), CancellationToken.None) ?? frame;
            var homeActivity = await ResumedActivityAsync(hub, entry.Serial);
            _log.LogInformation("Automation self-test: resumed activity before run: {Activity}", homeActivity);
            var fired = new TaskCompletionSource<TaskFiredInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFired(object? s, TaskFiredInfo e) => fired.TrySetResult(e);
            service.TaskFired += OnFired;
            service.TargetSerials.Clear();
            await page.ToggleRunAsync();
            if (!service.IsRunning)
            {
                _log.LogError("Automation self-test failed: the scheme did not start");
                return;
            }

            var winner = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            service.TaskFired -= OnFired;
            if (winner != fired.Task)
            {
                _log.LogError("Automation self-test failed: the task never fired");
                await page.ToggleRunAsync();
                return;
            }

            var info = await fired.Task;
            _log.LogInformation("Automation self-test: task fired on {Device}: {Task}/{Rule} score {Score:F3} at {At}",
                info.Run.Label, info.Args.TaskName, info.Args.RuleName, info.Args.Match?.Score, info.Args.Match?.Location);
            // The tap opens an app. Evidence: the picture changes, or the resumed activity is no longer the
            // launcher (emulators can take many seconds to render the new app). Wait up to 30 s.
            var diff = 0.0;
            var after = before;
            string? resumed = null;
            var leftHome = false;
            for (var i = 0; i < 60 && diff <= 8 && !leftHome; i++)
            {
                await Task.Delay(500);
                after = session.Latest ?? after;
                diff = SelfTestUtil.MeanDifference(before, after);
                if (i % 2 == 1)
                {
                    resumed = await ResumedActivityAsync(hub, entry.Serial);
                    leftHome = resumed is not null && homeActivity is not null && resumed != homeActivity;
                }

                _log.LogDebug("Automation self-test: poll {I} frame #{Seq} diff {Diff:F1} resumed {Resumed}", i, after.Sequence, diff, resumed);
            }

            _log.LogInformation("Automation self-test: frame #{Before} -> #{After}, video {Video}", before.Sequence, after.Sequence, session.VideoSize);
            if (Environment.GetEnvironmentVariable("CLICKZEN_SELFTEST_DUMP") is { Length: > 0 } dump)
            {
                await File.WriteAllBytesAsync(Path.Combine(dump, "before.png"), Vision.ImageCodec.ToPng(before));
                await File.WriteAllBytesAsync(Path.Combine(dump, "after.png"), Vision.ImageCodec.ToPng(after));
            }
            var rounds = service.Runs.Sum(r => r.Engine?.Rounds ?? 0);
            await page.ToggleRunAsync();
            var item = service.Tasks.FirstOrDefault();
            _log.LogInformation("Automation self-test: frame difference {Diff:F1}, resumed {Resumed}, rounds {Rounds}, fire count {Count}, running {Running}",
                diff, resumed, rounds, item?.FireCount, service.IsRunning);
            foreach (var line in service.Log.TakeLast(10))
            {
                _log.LogInformation("Automation self-test log: {Line}", line.ToString());
            }

            await GoHomeAsync(injector);
            if ((diff > 8 || leftHome) && item?.FireCount >= 1 && !service.IsRunning)
            {
                _log.LogInformation("Automation self-test passed");
                exit = 0;
            }
            else
            {
                _log.LogError("Automation self-test failed: the tap did not change the screen or the run did not stop");
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Automation self-test crashed");
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
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Automation self-test cleanup failed");
            }

            if (file is not null)
            {
                File.Delete(file);
            }

            Environment.Exit(exit);
        }
    }

    /// <summary>The top resumed activity ("package/.Activity"), or null when it cannot be read.</summary>
    internal static async Task<string?> ResumedActivityAsync(DeviceHub hub, string serial)
    {
        try
        {
            var text = await hub.Adb.ShellAsync(serial, "dumpsys activity activities | grep -m1 topResumedActivity", CancellationToken.None);
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\s(\S+/\S+)\s");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static async Task GoHomeAsync(Core.Input.ITouchInjector injector)
    {
        await injector.KeyAsync(Core.Input.KeyCodes.Home, CancellationToken.None);
        await Task.Delay(1500);
        await injector.KeyAsync(Core.Input.KeyCodes.Home, CancellationToken.None);
        await Task.Delay(1500);
    }

    /// <summary>Drives the workbench's pointer handlers with a drag from frame point a to b.</summary>
    internal static async Task<bool> DragAsync(Workbench bench, PointD a, PointD b)
    {
        if (bench.FrameToViewport(a) is not { } va || bench.FrameToViewport(b) is not { } vb)
        {
            return false;
        }

        const uint pointer = 9003;
        if (!bench.HandlePress(va, pointer))
        {
            return false;
        }

        for (var i = 1; i <= 10; i++)
        {
            var t = i / 10.0;
            bench.HandleMove(new Point(va.X + (vb.X - va.X) * t, va.Y + (vb.Y - va.Y) * t), pointer);
            await Task.Delay(10);
        }

        return bench.HandleRelease(vb, pointer);
    }

    /// <summary>
    /// Centre of the first icon in the home screen's dock row (a stock Pixel launcher puts the Phone app there,
    /// which opens without first-run screens): along the first dock column, the longest run of non-dark pixels
    /// between 74% and 92% of the height (the dock icons sit on the dark wallpaper; the run is the icon's circle).
    /// </summary>
    internal static PointD FindIcon(Frame f)
    {
        var cx = (int)(f.Width / 8.0);
        var y0 = (int)(f.Height * 0.74);
        var y1 = (int)(f.Height * 0.92);
        int bestStart = -1, bestLen = 0, runStart = -1;
        for (var y = y0; y <= y1; y++)
        {
            var (r, g, b) = f.PixelAt(cx, Math.Min(y, f.Height - 1));
            var lit = r + g + b > 240;
            if (lit && runStart < 0)
            {
                runStart = y;
            }

            if ((!lit || y == y1) && runStart >= 0)
            {
                var len = y - runStart;
                if (len > bestLen)
                {
                    bestLen = len;
                    bestStart = runStart;
                }

                runStart = -1;
            }
        }

        return bestLen >= f.Width * 0.06
            ? new PointD(cx, bestStart + bestLen / 2.0)
            : new PointD(cx, f.Height * 0.82);
    }
}
