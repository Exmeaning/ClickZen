using ClickZen.Core.Automation;
using ClickZen.Core.Geometry;
using ClickZen.Device.Scrcpy;
using ClickZen.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Frame = ClickZen.Core.Automation.Frame;

namespace ClickZen.App.Services;

/// <summary>
/// Developer self-check for per-task screen orientation (<c>--selftest-orientation</c>), on the automation page with
/// the current streaming device. The device must show the Android Settings app in landscape when rotation is locked to
/// 90° (a stock emulator image does; its launcher stays portrait).
/// <list type="number">
/// <item>Portrait: new scheme, task A gets a tap point → RefSize = portrait, A = Auto (follows the scheme).</item>
/// <item>Landscape (Settings): capture "Network &amp; internet" for an image condition of task B → B is bound to
/// landscape automatically; the template, area and tap point are in landscape reference pixels; the live test matches.</item>
/// <item>Landscape: picking for task A asks; Cancel / "template only" / Switch behave as designed.</item>
/// <item>The editor's orientation box and hint, and the task list summary, follow the task.</item>
/// <item>Run in landscape: B fires and opens the Network page; A (portrait, other orientation) is skipped and logged.</item>
/// <item>Run in portrait: B is skipped, a coordinate-free task runs.</item>
/// <item>Save as format 2, reopen, orientations are kept.</item>
/// </list>
/// Exit code 0 = pass, 2 = fail, 4 = no device. Restores the rotation and goes home at the end.
/// </summary>
internal sealed class OrientationSelfTest
{
    private const string SettingsActivity = "com.android.settings/.Settings";
    private const string NetworkLabel = "Network & internet";

    private readonly Shell.MainWindow _window;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private DeviceHub? _hub;
    private string _serial = "";
    private int _failures;

    public OrientationSelfTest(Shell.MainWindow window, IServiceProvider services, ILogger log)
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
            _hub = _services.GetRequiredService<DeviceHub>();
            await SelfTestUtil.UseTestDeviceAsync(_hub);
            var entry = await SelfTestUtil.WaitForAsync(() => _hub.Current is { Session.State: SessionState.Streaming } c ? c : null, TimeSpan.FromSeconds(60));
            if (entry?.Session is not { } session)
            {
                _log.LogError("Orientation self-test: no streaming device");
                exit = 4;
                return;
            }

            _serial = entry.Serial;
            _window.NavigateTo("automation");
            page = await SelfTestUtil.WaitForAsync(() => _window.Frame.Content as Views.AutomationPage, TimeSpan.FromSeconds(10));
            var bench = page?.Workbench;
            if (page is null || bench is null || await SelfTestUtil.WaitForAsync(() => bench.CurrentFrame, TimeSpan.FromSeconds(15)) is null)
            {
                _log.LogError("Orientation self-test: the automation page never showed a frame");
                return;
            }

            service = page.Service;
            var host = (IAutomationEditorHost)page;
            var injector = await _hub.GetInjectorAsync(entry);
            service.TargetSerials.Clear();
            service.CheckIntervalMs = 200;

            // ---------------------------------------------------------- 1. portrait: task A
            await RotateAsync(0);
            await AutomationSelfTest.GoHomeAsync(injector);
            var portrait = await WaitForScreenAsync(entry, landscape: false);
            service.New();
            service.Scheme.Settings.Humanize = Core.Input.HumanizeOptions.None;
            Check(portrait is { IsLandscape: false }, $"device is portrait at the start ({portrait})");
            var taskA = new AutomationTask { Name = "A portrait", CooldownMs = 0, Rules = [new Rule { Name = "tap", Then = [new TapAction()] }] };
            var itemA = service.AddTask(taskA);
            page.SelectTask(itemA);
            await Task.Delay(300);
            var tapA = (TapAction)taskA.Rules[0].Then[0];
            var picked = await PickPointAsync(host, bench, new PointD(0.5, 0.3));
            if (picked is { } pa)
            {
                tapA.Target.Point = pa;
            }

            var schemeRef = service.Scheme.RefSize;
            _log.LogInformation("Orientation self-test: portrait pick {Point}, RefSize {Ref}, A orientation {O}", picked, schemeRef, taskA.Orientation);
            Check(picked is not null, "a point can be picked in portrait");
            Check(schemeRef == entry.ScreenSize && !schemeRef.IsLandscape, $"RefSize adopts the portrait screen ({schemeRef} vs {entry.ScreenSize})");
            Check(taskA.Orientation == TaskOrientation.Auto && TaskOrientations.Effective(taskA, schemeRef) == TaskOrientation.Portrait,
                "task A follows the scheme (portrait) without an explicit setting");
            if (picked is { } p0)
            {
                var expected = new PointI(schemeRef.Width / 2, (int)(schemeRef.Height * 0.3));
                Check(Math.Abs(p0.X - expected.X) <= schemeRef.Width / 50 && Math.Abs(p0.Y - expected.Y) <= schemeRef.Height / 50,
                    $"the portrait point is in portrait reference pixels ({p0} ≈ {expected})");
            }

            // ---------------------------------------------------------- 2. landscape: task B bound automatically
            await OpenSettingsLandscapeAsync();
            var land = await WaitForScreenAsync(entry, landscape: true);
            Check(land is { IsLandscape: true }, $"device turned landscape ({land})");
            var landRef = schemeRef.Transposed;
            var target2 = await FindNodeAsync(NetworkLabel);
            Check(target2 is not null, $"\"{NetworkLabel}\" found in the landscape UI dump");
            var taskB = new AutomationTask { Name = "B landscape", CooldownMs = 0 };
            var condition = new ImageCondition { Threshold = 0.85 };
            taskB.Rules.Add(new Rule
            {
                Name = "open network",
                When = new ConditionGroup { Conditions = [condition] },
                Then = [new TapAction { Target = new Target { Kind = TargetKind.LastMatch } }],
            });
            var itemB = service.AddTask(taskB);
            page.SelectTask(itemB);
            await Task.Delay(300);
            Check(page.Editor?.OrientationState.Selected == TaskOrientation.Auto, "B's orientation box starts at Automatic");

            (TemplateAsset Template, RectI Area)? captured = null;
            var cut = default(RectI);
            if (target2 is { } label)
            {
                bench.Refresh();
                bench.SetFrozen(true);
                await Task.Delay(400);
                var frame = bench.CurrentFrame!;
                var screen = entry.ScreenSize;
                cut = new RectI(label.Bounds.X - 12, label.Bounds.Y - 12, label.Bounds.Width + 24, label.Bounds.Height + 24);
                page.AutoTemplateName = "Network";
                var capture = host.CaptureTemplateAsync();
                await Task.Delay(100);
                var ok = await AutomationSelfTest.DragAsync(bench, DeviceToFrame(cut.X, cut.Y, frame, screen), DeviceToFrame(cut.Right, cut.Bottom, frame, screen));
                Check(ok, "the workbench accepted the landscape drag");
                captured = ok ? await capture : null;
                bench.SetFrozen(false);
            }

            Check(captured is not null, "a template can be captured in landscape");
            if (captured is { } got)
            {
                condition.TemplateId = got.Template.Id;
                condition.Area = got.Area.IsEmpty ? default : new RectI(Math.Max(0, got.Area.X - 40), Math.Max(0, got.Area.Y - 40), got.Area.Width + 80, got.Area.Height + 80).ClampTo(landRef);
                _log.LogInformation("Orientation self-test: landscape capture area {Area} (landscape ref {Ref}), template {Size}, source orientation {O}, B orientation {B}",
                    got.Area, landRef, got.Template.Size, got.Template.SourceOrientation, taskB.Orientation);
                Check(taskB.Orientation == TaskOrientation.Landscape, "B was bound to landscape by its first coordinate");
                Check(got.Template.SourceOrientation == TaskOrientation.Landscape, "the template remembers it was cut in landscape");
                // The landscape screen is the scheme's landscape reference (same device), so the area must be the cut
                // rectangle itself: in portrait reference pixels it would be squeezed (x · 1080/2400, y · 2400/1080).
                var tolerance = Math.Max(4, landRef.Width / 200);
                Check(!got.Area.IsEmpty && Math.Abs(got.Area.X - cut.X) <= tolerance && Math.Abs(got.Area.Y - cut.Y) <= tolerance
                      && Math.Abs(got.Area.Width - cut.Width) <= tolerance && Math.Abs(got.Area.Height - cut.Height) <= tolerance
                      && got.Area.Right <= landRef.Width && got.Area.Bottom <= landRef.Height,
                    $"the area is the dragged rectangle in landscape reference pixels ({got.Area} ≈ {cut} in {landRef})");
                Check(got.Template.Size == got.Area.Size, $"the template has the area's size ({got.Template.Size} vs {got.Area.Size})");
                Check(page.Editor?.OrientationState.Selected == TaskOrientation.Landscape, "B's orientation box now shows Landscape only");
                Check(itemB.Summary.Contains(Loc("Auto_Orientation_Landscape"), StringComparison.Ordinal), $"B's list summary shows landscape ({itemB.Summary})");
                Check(!itemA.Summary.Contains(Loc("Auto_Orientation_Landscape"), StringComparison.Ordinal), $"A's list summary does not show landscape ({itemA.Summary})");

                var test = await host.TestConditionAsync(condition);
                _log.LogInformation("Orientation self-test: landscape live test matched={Matched} score={Score:F3} at {Loc}", test.Matched, test.Score, test.Location);
                Check(test.Matched && test.Location is { } loc && Math.Abs(loc.X - got.Area.X) <= 8 && Math.Abs(loc.Y - got.Area.Y) <= 8,
                    "the landscape live test finds the template where it was cut");
                await SnapshotAsync("editor-landscape-task.png");
            }

            // ---------------------------------------------------------- 3. landscape: picking for portrait task A asks
            page.SelectTask(itemA);
            await Task.Delay(300);
            var oldPoint = tapA.Target.Point;
            var asked = 0;
            page.AutoOrientationAnswer = _ => { asked++; return ContentDialogResult.None; };
            var cancelled = await PickPointAsync(host, bench, new PointD(0.5, 0.5));
            Check(asked == 1 && cancelled is null && taskA.Orientation == TaskOrientation.Auto && tapA.Target.Point == oldPoint,
                $"Cancel keeps portrait task A unchanged (asked {asked}, result {cancelled})");

            asked = 0;
            page.AutoOrientationAnswer = _ => { asked++; return ContentDialogResult.Secondary; };
            page.AutoTemplateName = "Only template";
            var templatesBefore = service.Scheme.Templates.Count;
            (TemplateAsset Template, RectI Area)? onlyTemplate = null;
            if (bench.CurrentFrame is { } f2)
            {
                var capture2 = host.CaptureTemplateAsync();
                await Task.Delay(100);
                if (await AutomationSelfTest.DragAsync(bench, new PointD(f2.Width * 0.6, f2.Height * 0.4), new PointD(f2.Width * 0.7, f2.Height * 0.55)))
                {
                    onlyTemplate = await capture2;
                }
            }

            Check(asked == 1 && onlyTemplate is { Area.IsEmpty: true } && service.Scheme.Templates.Count == templatesBefore + 1 && taskA.Orientation == TaskOrientation.Auto,
                $"\"Template only\" adds the template without seeding an area or moving task A (asked {asked})");

            asked = 0;
            page.AutoOrientationAnswer = _ => { asked++; return ContentDialogResult.Primary; };
            var switched = await PickPointAsync(host, bench, new PointD(0.25, 0.5));
            Check(asked == 1 && switched is not null && taskA.Orientation == TaskOrientation.Landscape,
                $"Switch makes task A a landscape task (asked {asked}, point {switched})");
            Check(page.Editor?.OrientationState.Selected == TaskOrientation.Landscape, "A's orientation box follows the switch");
            await SnapshotAsync("editor-switched.png");
            if (switched is { } ps)
            {
                Check(Math.Abs(ps.X - landRef.Width / 4) <= landRef.Width / 50 && Math.Abs(ps.Y - landRef.Height / 2) <= landRef.Height / 50,
                    $"the switched point is in landscape reference pixels ({ps} in {landRef})");
            }

            // Back to portrait for A through the editor's box; its hint explains it.
            page.AutoOrientationAnswer = null;
            page.Editor?.SelectOrientation(TaskOrientation.Portrait);
            await Task.Delay(200);
            tapA.Target.Point = oldPoint;
            Check(taskA.Orientation == TaskOrientation.Portrait, "the orientation box sets Portrait only");
            Check(page.Editor?.OrientationState.Hint.Contains(Loc("Auto_OrientationWord_Portrait"), StringComparison.Ordinal) == true,
                $"the hint mentions portrait ({page.Editor?.OrientationState.Hint})");
            page.Editor?.SelectOrientation(TaskOrientation.Auto);
            await Task.Delay(200);
            Check(taskA.Orientation == TaskOrientation.Auto && TaskOrientations.Effective(taskA, schemeRef) == TaskOrientation.Portrait,
                "Automatic makes A follow the scheme again");

            // A frozen landscape picture stays landscape after the device turned back to portrait.
            bench.Refresh();
            bench.SetFrozen(true);
            await Task.Delay(300);
            await RotateAsync(0);
            var portraitAgain = await WaitForDeviceAsync(entry, landscape: false);
            var taskD = new AutomationTask { Name = "D frozen", CooldownMs = 0, Rules = [new Rule { Then = [new TapAction()] }] };
            var itemD = service.AddTask(taskD);
            page.SelectTask(itemD);
            await Task.Delay(300);
            var frozenPick = await PickPointAsync(host, bench, new PointD(0.75, 0.5));
            _log.LogInformation("Orientation self-test: frozen landscape picture, device {Screen}, pick {Point}, D orientation {O}",
                portraitAgain, frozenPick, taskD.Orientation);
            Check(portraitAgain is { IsLandscape: false } && bench.CurrentFrame is { Size.IsLandscape: true },
                "the device is portrait again while the frozen picture is landscape");
            Check(frozenPick is { } pf && taskD.Orientation == TaskOrientation.Landscape
                  && Math.Abs(pf.X - landRef.Width * 3 / 4) <= landRef.Width / 50 && Math.Abs(pf.Y - landRef.Height / 2) <= landRef.Height / 50,
                $"a pick on the frozen landscape picture is a landscape coordinate ({frozenPick} in {landRef})");
            bench.SetFrozen(false);
            service.DeleteTask(itemD);
            page.SelectTask(itemA);

            // MCP find_image with the scheme's landscape template: the template keeps its device size in either orientation.
            await OpenSettingsLandscapeAsync();
            await WaitForScreenAsync(entry, landscape: true);
            var mcp = _services.GetRequiredService<McpBackend>();
            var mcpMatch = await mcp.FindImageAsync(_serial, new McpImageQuery("Network", null, null, 0.85, false, false), CancellationToken.None);
            var node = await FindNodeAsync(NetworkLabel);
            _log.LogInformation("Orientation self-test: MCP find_image in landscape found={Found} score={Score} at {Loc}, label at {Label}",
                mcpMatch.Found, mcpMatch.Score, mcpMatch.Location, node?.Bounds);
            Check(mcpMatch.Found && mcpMatch.Center is { } mc && node is { } n && n.Bounds.Contains(mc),
                "MCP find_image finds the scheme template on the landscape screen");
            await RotateAsync(0);
            await WaitForScreenAsync(entry, landscape: false);
            var portraitNode = await FindNodeAsync(NetworkLabel);
            var mcpPortrait = await mcp.FindImageAsync(_serial, new McpImageQuery("Network", null, null, 0.85, false, false), CancellationToken.None);
            _log.LogInformation("Orientation self-test: MCP find_image in portrait found={Found} score={Score} at {Loc}, label at {Label}",
                mcpPortrait.Found, mcpPortrait.Score, mcpPortrait.Location, portraitNode?.Bounds);
            Check(portraitNode is null || (mcpPortrait.Found && mcpPortrait.Center is { } pc && portraitNode.Bounds.Contains(pc)),
                "the landscape-cut template is also found on the portrait screen (same pixel density)");

            // A coordinate-free task: runs in both orientations.
            var taskC = new AutomationTask
            {
                Name = "C both",
                CooldownMs = 0,
                Rules = [new Rule { Name = "log", Then = [new SetVariableAction { Variable = "c", Expression = "c + 1" }] }],
            };
            service.Scheme.Variables.Add(new VariableDefinition { Name = "c", Type = Core.Variables.VariableType.Int });
            service.AddTask(taskC);

            // ---------------------------------------------------------- 4. run in landscape
            await OpenSettingsLandscapeAsync();
            await WaitForScreenAsync(entry, landscape: true);
            var before = await ResumedAsync();
            service.ClearLog();
            var firedB = new TaskCompletionSource<TaskFiredInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firedNames = new List<string>();
            void OnFired(object? s, TaskFiredInfo e)
            {
                firedNames.Add(e.Args.TaskName);
                if (e.Args.TaskName == taskB.Name)
                {
                    firedB.TrySetResult(e);
                }
            }

            service.TaskFired += OnFired;
            await page.ToggleRunAsync();
            var winner = await Task.WhenAny(firedB.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Check(winner == firedB.Task, "landscape task B fires in landscape");
            string? after = null;
            for (var i = 0; i < 20 && (after is null || after == before); i++)
            {
                await Task.Delay(500);
                after = await ResumedAsync();
            }

            await Task.Delay(1000);
            await page.ToggleRunAsync();
            service.TaskFired -= OnFired;
            var log = service.Log.Select(l => l.Message).ToList();
            _log.LogInformation("Orientation self-test: landscape run fired [{Fired}], resumed {Before} -> {After}", string.Join(", ", firedNames.Distinct()), before, after);
            foreach (var line in service.Log.TakeLast(8))
            {
                _log.LogInformation("Orientation self-test log: {Line}", line.ToString());
            }

            Check(after is not null && after != before, $"B's tap opened another page ({before} -> {after})");
            Check(!firedNames.Contains(taskA.Name), "portrait task A did not run in landscape");
            Check(firedNames.Contains(taskC.Name), "coordinate-free task C ran in landscape");
            Check(log.Any(m => m.Contains("skipping", StringComparison.Ordinal) && m.Contains(taskA.Name, StringComparison.Ordinal)),
                "the run log names the skipped portrait task");

            // ---------------------------------------------------------- 5. run in portrait
            await RotateAsync(0);
            await AutomationSelfTest.GoHomeAsync(injector);
            await WaitForScreenAsync(entry, landscape: false);
            taskA.Enabled = false; // its tap would open something on the launcher
            firedNames.Clear();
            service.TaskFired += OnFired;
            await page.ToggleRunAsync();
            await Task.Delay(2500);
            await page.ToggleRunAsync();
            service.TaskFired -= OnFired;
            _log.LogInformation("Orientation self-test: portrait run fired [{Fired}]", string.Join(", ", firedNames.Distinct()));
            Check(!firedNames.Contains(taskB.Name), "landscape task B did not run in portrait");
            Check(firedNames.Contains(taskC.Name), "coordinate-free task C ran in portrait");
            taskA.Enabled = true;

            // ---------------------------------------------------------- 6. save and reopen
            file = Path.Combine(Path.GetTempPath(), $"cz-orientation-{Guid.NewGuid():N}{SchemePackage.FileExtension}");
            service.Rename("Orientation self-test");
            service.Save(file);
            service.New();
            service.Open(file);
            var reopened = service.Scheme;
            var rb = reopened.Tasks.FirstOrDefault(t => t.Name == taskB.Name);
            var ra = reopened.Tasks.FirstOrDefault(t => t.Name == taskA.Name);
            var rt = reopened.Templates.FirstOrDefault(t => t.Name == "Network");
            Check(reopened.FormatVersion == 2, $"a scheme with a landscape task is saved as format 2 ({reopened.FormatVersion})");
            Check(rb?.Orientation == TaskOrientation.Landscape && ra?.Orientation == TaskOrientation.Auto && rt?.SourceOrientation == TaskOrientation.Landscape,
                "orientations survive save and reopen");
            Check(reopened.RefSize == schemeRef, $"RefSize survives ({reopened.RefSize})");

            if (_failures == 0)
            {
                _log.LogInformation("Orientation self-test passed");
                exit = 0;
            }
            else
            {
                _log.LogError("Orientation self-test failed: {Count} check(s) failed", _failures);
            }
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Orientation self-test crashed");
        }
        finally
        {
            try
            {
                if (page is not null)
                {
                    page.AutoOrientationAnswer = null;
                }

                if (service is not null)
                {
                    if (service.IsRunning)
                    {
                        await service.StopAsync();
                    }

                    service.New();
                }

                if (_hub is not null && _serial.Length > 0)
                {
                    await _hub.Adb.ShellAsync(_serial, "wm user-rotation free; input keyevent 3", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Orientation self-test cleanup failed");
            }

            if (file is not null)
            {
                File.Delete(file);
            }

            Environment.Exit(exit);
        }
    }

    private void Check(bool ok, string what)
    {
        if (ok)
        {
            _log.LogInformation("Orientation self-test OK: {What}", what);
        }
        else
        {
            _failures++;
            _log.LogError("Orientation self-test FAILED: {What}", what);
        }
    }

    private string Loc(string key) => _services.GetRequiredService<ILocalizer>()[key];

    /// <summary>Saves a picture of the app window to CLICKZEN_SELFTEST_DUMP (for looking at the editor UI).</summary>
    private async Task SnapshotAsync(string name)
    {
        if (Environment.GetEnvironmentVariable("CLICKZEN_SELFTEST_DUMP") is not { Length: > 0 } dir)
        {
            return;
        }

        try
        {
            await Task.Delay(400); // let layout and the InfoBar settle
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            using var capture = new Platform.Capture.PrintWindowCapture(hwnd);
            if (capture.CaptureOnce() is { } frame)
            {
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, name), Vision.ImageCodec.ToPng(frame));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Orientation self-test: snapshot {Name} failed", name);
        }
    }

    private Task<string> ShellAsync(string command) => _hub!.Adb.ShellAsync(_serial, command, TimeSpan.FromSeconds(20), CancellationToken.None);

    private async Task RotateAsync(int rotation)
    {
        await ShellAsync($"wm user-rotation lock {rotation}");
        await Task.Delay(1500);
    }

    /// <summary>Restarts Settings and turns the screen to landscape (the launcher itself stays portrait).</summary>
    private async Task OpenSettingsLandscapeAsync()
    {
        await ShellAsync($"am start -S -W -n {SettingsActivity}");
        await RotateAsync(1);
    }

    private async Task<string?> ResumedAsync() => await AutomationSelfTest.ResumedActivityAsync(_hub!, _serial);

    /// <summary>Waits until the device screen and the newest frame both have the orientation.</summary>
    private static async Task<SizeI?> WaitForScreenAsync(DeviceEntry entry, bool landscape)
    {
        for (var i = 0; i < 50; i++)
        {
            var s = entry.ScreenSize;
            var f = entry.Frames?.Latest?.Size ?? default;
            if (!s.IsEmpty && !f.IsEmpty && s.IsLandscape == landscape && f.IsLandscape == landscape)
            {
                await Task.Delay(700); // let the picture settle after the rotation animation
                return s;
            }

            await Task.Delay(200);
        }

        return null;
    }

    /// <summary>Waits until the device reports the orientation (the frozen workbench picture is not looked at).</summary>
    private static async Task<SizeI?> WaitForDeviceAsync(DeviceEntry entry, bool landscape)
    {
        for (var i = 0; i < 50; i++)
        {
            var s = entry.ScreenSize;
            if (!s.IsEmpty && s.IsLandscape == landscape)
            {
                return s;
            }

            await Task.Delay(200);
        }

        return null;
    }

    /// <summary>Bounds of the first node with the text (device pixels of the current orientation).</summary>
    private async Task<UiNode?> FindNodeAsync(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var xml = await ShellAsync("uiautomator dump /sdcard/cz-ui.xml >/dev/null 2>&1; cat /sdcard/cz-ui.xml; rm -f /sdcard/cz-ui.xml");
            var node = UiHierarchy.Parse(xml).FirstOrDefault(n => string.Equals(n.Text, text, StringComparison.Ordinal));
            if (node is not null)
            {
                return node;
            }

            await Task.Delay(1000);
        }

        return null;
    }

    private static PointD DeviceToFrame(int x, int y, Frame frame, SizeI screen) =>
        new FrameToDevice(frame.Size, screen).DeviceToFramePoint(new PointD(x, y));

    /// <summary>Picks a point at a fraction of the picture through the workbench's pointer handlers.</summary>
    private static async Task<PointI?> PickPointAsync(IAutomationEditorHost host, Controls.Workbench bench, PointD fraction)
    {
        if (bench.CurrentFrame is not { } f)
        {
            return null;
        }

        var pick = host.PickPointAsync();
        await Task.Delay(100);
        if (bench.FrameToViewport(new PointD(f.Width * fraction.X, f.Height * fraction.Y)) is not { } v || !bench.HandlePress(v, 9101))
        {
            bench.CancelPick();
        }

        return await pick;
    }
}
