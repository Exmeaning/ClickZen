using ClickZen.App.Services;
using ClickZen.App.Views;
using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using ClickZen.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Windows.System;
using WinUIEx;

namespace ClickZen.App.Shell;

public sealed partial class MainWindow
{
    private SettingsService _settings = null!;
    private TrayIcon? _tray;
    private bool _allowClose;
    private bool _exitDialog;

    public void SetInfoAction(ButtonBase action) => GlobalInfoBar.ActionButton = action;

    private void InitializeDesktopFeatures(IServiceProvider services)
    {
        _settings = services.GetRequiredService<SettingsService>();
        InitializeWindowPlacement(services.GetRequiredService<AppPaths>());
        AppWindow.Closing += (sender, e) =>
        {
            if (_allowClose || App.IsSelfCheck) return;
            e.Cancel = true;
            if (_settings.Current.General.MinimizeToTray) HideToTray();
            else _ = RequestExitAsync();
        };
        var manager = WindowManager.Get(this);
        manager.WindowStateChanged += (_, state) =>
        {
            if (!App.IsSelfCheck && state == WindowState.Minimized && _settings.Current.General.MinimizeToTray) HideToTray();
        };
        _settings.Changed += (_, _) =>
        {
            if (!_settings.Current.General.MinimizeToTray && _tray is not null)
            {
                RestoreFromTray();
                _tray.Dispose();
                _tray = null;
            }
        };
        Closed += (_, _) => _tray?.Dispose();
        AddAccelerator(VirtualKey.F9, VirtualKeyModifiers.None);
        AddAccelerator(VirtualKey.F10, VirtualKeyModifiers.None);
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None);
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control);
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control);
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control);
    }

    private void InitializeWindowPlacement(AppPaths paths)
    {
        if (App.IsSelfCheck) return;
        var file = Path.Combine(paths.Root, "window.json");
        try
        {
            if (File.Exists(file))
            {
                var rect = System.Text.Json.JsonSerializer.Deserialize<int[]>(File.ReadAllText(file));
                if (rect is { Length: 4 } && rect[2] >= 1100 && rect[3] >= 700)
                {
                    var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(rect[0], rect[1]), DisplayAreaFallback.Nearest);
                    var x = Math.Clamp(rect[0], area.WorkArea.X, Math.Max(area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - 100));
                    var y = Math.Clamp(rect[1], area.WorkArea.Y, Math.Max(area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - 100));
                    AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, rect[2], rect[3]));
                }
            }
        }
        catch (Exception) { /* Corrupt placement must never prevent startup. */ }
        AppWindow.Changed += (_, _) =>
        {
            if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
            var p = AppWindow.Position;
            var size = AppWindow.Size;
            try { AtomicFile.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new[] { p.X, p.Y, size.Width, size.Height })); }
            catch (Exception) { /* Window persistence is best effort. */ }
        };
    }

    private void HideToTray()
    {
        if (_tray is null)
        {
            _tray = new TrayIcon(1, Path.Combine(AppContext.BaseDirectory, "Assets", "ClickZen.ico"), "ClickZen");
            _tray.LeftDoubleClick += (_, _) => RestoreFromTray();
            _tray.Selected += (_, _) => RestoreFromTray();
            _tray.ContextMenu += (_, e) =>
            {
                var menu = new MenuFlyout();
                var show = new MenuFlyoutItem { Text = _loc["Settings_TrayShow"] };
                show.Click += (_, _) => RestoreFromTray();
                var exit = new MenuFlyoutItem { Text = _loc["Settings_Exit"] };
                exit.Click += async (_, _) => { RestoreFromTray(); await RequestExitAsync(); };
                menu.Items.Add(show);
                menu.Items.Add(exit);
                e.Flyout = menu;
            };
        }
        _tray.IsVisible = true;
        AppWindow.Hide();
    }

    private void RestoreFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
    }

    private async Task RequestExitAsync()
    {
        if (_exitDialog) return;
        var needsConfirmation = _settings.Current.General.ConfirmOnExit || _automation.IsRunning || _automation.IsStepping
            || _automation.IsDirty || _recording.IsRecording || _recording.IsPlaying || _recording.IsDirty;
        if (needsConfirmation && !App.IsSelfCheck)
        {
            _exitDialog = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot, Title = _loc["Settings_ExitTitle"],
                    Content = _loc["Settings_ExitMessage"], PrimaryButtonText = _loc["Settings_Exit"],
                    CloseButtonText = _loc["Common_Cancel"], DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            catch (Exception) { return; }
            finally { _exitDialog = false; }
        }
        _allowClose = true;
        Close();
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += async (_, e) =>
        {
            e.Handled = true;
            try
            {
                if (key == (VirtualKey)188) { NavigateTo("settings"); return; }
                if (key == VirtualKey.S && modifiers.HasFlag(VirtualKeyModifiers.Shift)) { await SaveScreenshotAsync(); return; }
                if (ContentFrame.Content is RecordingPage recording)
                {
                    switch (key)
                    {
                        case VirtualKey.F9: recording.ToggleRecording(); break;
                        case VirtualKey.F10: await recording.TogglePlaybackAsync(); break;
                        case VirtualKey.S: await recording.SaveDocumentAsync(); break;
                        case VirtualKey.O: await recording.OpenDocumentAsync(); break;
                    }
                }
                else if (ContentFrame.Content is AutomationPage automation)
                {
                    switch (key)
                    {
                        case VirtualKey.F5: await automation.ToggleRunAsync(); break;
                        case VirtualKey.S: await automation.SaveDocumentAsync(); break;
                        case VirtualKey.O: await automation.OpenDocumentAsync(); break;
                    }
                }
            }
            catch (Exception ex) { ShowInfo(_loc.Format("Settings_CommandFailed", ex.Message), InfoBarSeverity.Error); }
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private async Task SaveScreenshotAsync()
    {
        if (_hub.Current?.Frames?.Latest is not { } frame)
        {
            ShowInfo(_loc["Mirror_NoFrame"], InfoBarSeverity.Warning);
            return;
        }
        var paths = App.Current.Services.GetRequiredService<AppPaths>();
        Directory.CreateDirectory(paths.ScreenshotsDirectory);
        var path = Path.Combine(paths.ScreenshotsDirectory, $"ClickZen-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var png = await Task.Run(() => ImageCodec.ToPng(frame));
        await File.WriteAllBytesAsync(path, png);
        ShowInfo(_loc.Format("Mirror_ScreenshotSaved", path), InfoBarSeverity.Success);
    }
}
