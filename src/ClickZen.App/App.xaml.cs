using ClickZen.App.Services;
using ClickZen.App.Services.Logging;
using ClickZen.App.Shell;
using ClickZen.Core;
using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using ClickZen.Device;
using ClickZen.Device.Adb;
using ClickZen.Device.Scrcpy;
using ClickZen.Vision;
using ClickZen.Core.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Serilog.Events;

namespace ClickZen.App;

public partial class App : Application
{
    private readonly ServiceProvider _services;
    private readonly Microsoft.Extensions.Logging.ILogger<App> _log;
    private readonly CrashReporter _crash;
    private MainWindow? _window;

    public App()
    {
        var paths = new AppPaths(Environment.GetEnvironmentVariable("CLICKZEN_DATA_DIR"));
        paths.EnsureCreated();

        var memorySink = new InMemoryLogSink();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.File(
                Path.Combine(paths.LogsDirectory, "clickzen-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture))
            .WriteTo.Sink(memorySink)
            .CreateLogger();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.ClearProviders().AddSerilog(serilog, dispose: true).SetMinimumLevel(LogLevel.Debug));
        sc.AddSingleton(paths);
        sc.AddSingleton(memorySink);
        sc.AddSingleton<CrashReporter>();
        sc.AddSingleton<SettingsService>();
        sc.AddSingleton<ThemeService>();
        sc.AddSingleton<ILocalizer, Localizer>();
        sc.AddSingleton(new BundledTools());

        // Devices
        sc.AddSingleton<AdbServerHost>();
        sc.AddSingleton<AdbService>();
        sc.AddSingleton<DeviceWatcher>();
        sc.AddSingleton<SavedDeviceStore>();
        sc.AddSingleton<AutoConnector>();
        sc.AddSingleton<IScrcpyTransport, AdbScrcpyTransport>();
        sc.AddSingleton<DeviceHub>();
        sc.AddSingleton<RecordingService>();
        sc.AddSingleton<IDeviceTouchCapture, GeteventTouchCapture>();
        sc.AddSingleton(_ => new ClickZen.Core.Variables.VariableStore());
        sc.AddSingleton<VariableSyncService>();
        sc.AddSingleton<AutomationService>();
        sc.AddTransient<ClickZen.App.ViewModels.DevicesViewModel>();
        sc.AddTransient<ClickZen.App.ViewModels.LogsViewModel>();

        // Vision
        sc.AddSingleton<IImageMatcher, OpenCvImageMatcher>();
        _services = sc.BuildServiceProvider();

        _log = _services.GetRequiredService<ILogger<App>>();
        _crash = _services.GetRequiredService<CrashReporter>();
        Localizer.ApplyLanguage(_services.GetRequiredService<SettingsService>().Current.Appearance.Language);

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _log.LogCritical(ex, "AppDomain unhandled exception");
                _crash.Write(ex, "AppDomain");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        InitializeComponent();
        _log.LogInformation("ClickZen {Version} starting, data dir {Dir}", AppInfo.Version, paths.Root);
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _services;

    public MainWindow? MainWindow => _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_services);
        _window.Closed += OnMainWindowClosed;
        _window.Activate();

        var missing = _services.GetRequiredService<BundledTools>().MissingFiles();
        if (missing.Count > 0)
        {
            _log.LogError("Bundled tools missing: {Missing}", string.Join(", ", missing));
        }

        if (IsSmokeTest)
        {
            // Never touch the user's adb server from an automated self-check.
            _ = new SmokeTest(_window, _services, _log).RunAsync();
            return;
        }

        _ = StartDevicesAsync();
        _ = _services.GetRequiredService<VariableSyncService>().InitializeAsync(_window.DispatcherQueue);

        if (Environment.GetCommandLineArgs().Contains("--selftest-input", StringComparer.OrdinalIgnoreCase))
        {
            _ = new InputSelfTest(_window, _services, _log).RunAsync();
        }

        if (Environment.GetCommandLineArgs().Contains("--selftest-recording", StringComparer.OrdinalIgnoreCase))
        {
            _ = new RecordingSelfTest(_window, _services, _log).RunAsync();
        }

        if (Environment.GetCommandLineArgs().Contains("--selftest-automation", StringComparer.OrdinalIgnoreCase))
        {
            _ = new AutomationSelfTest(_window, _services, _log).RunAsync();
        }

        // Developer convenience: `ClickZen.exe --page mirror` opens a page directly.
        var cli = Environment.GetCommandLineArgs();
        var pageIndex = Array.FindIndex(cli, a => string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase));
        if (pageIndex >= 0 && pageIndex + 1 < cli.Length)
        {
            _window.NavigateTo(cli[pageIndex + 1].ToLowerInvariant());
        }
    }

    public static bool IsSmokeTest { get; } = Environment.GetCommandLineArgs().Contains("--smoke", StringComparer.OrdinalIgnoreCase);

    private async Task StartDevicesAsync()
    {
        try
        {
            await _services.GetRequiredService<DeviceHub>().StartAsync(_window!.DispatcherQueue);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Device subsystem failed to start");
        }
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            await _services.GetRequiredService<AutomationService>().StopAsync();
            await _services.GetRequiredService<VariableSyncService>().DisposeAsync();
            await _services.GetRequiredService<DeviceHub>().DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error while shutting down devices");
        }
        finally
        {
            await _services.DisposeAsync();
        }
    }

    private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _log.LogCritical(e.Exception, "Unhandled UI exception");
        var file = _crash.Write(e.Exception, "UI");
        e.Handled = true;

        if (IsSmokeTest)
        {
            Environment.Exit(3);
        }

        if (_window?.Content?.XamlRoot is { } root)
        {
            var loc = _services.GetRequiredService<ILocalizer>();
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = loc["Error_Title"],
                Content = loc.Format("Error_CrashSaved", file ?? "-"),
                CloseButtonText = loc["Common_Close"],
                PrimaryButtonText = file is null ? null : loc["Error_OpenFolder"],
            };
            try
            {
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && file is not null)
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
                }
            }
            catch (Exception)
            {
                // Another dialog may already be open; the report is saved regardless.
            }
        }
    }
}
