using System.Reflection;
using ClickZen.App.Services;
using ClickZen.Core;
using ClickZen.Core.Persistence;
using ClickZen.Core.Settings;
using ClickZen.Device.Scrcpy;
using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace ClickZen.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsService _settings;
    private readonly ILocalizer _loc;
    private readonly LanguagePreference _startupLanguage;

    public SettingsPage()
    {
        _settings = App.Current.Services.GetRequiredService<SettingsService>();
        _loc = App.Current.Services.GetRequiredService<ILocalizer>();
        _startupLanguage = _settings.Current.Appearance.Language;
        InitializeComponent();
        BuildGroups();
    }

    private void BuildGroups()
    {
        while (SettingsHost.Children.Count > 2)
        {
            SettingsHost.Children.RemoveAt(2);
        }

        foreach (var group in typeof(AppSettings).GetProperties().Where(p => p.Name != nameof(AppSettings.FormatVersion)))
        {
            var expander = new SettingsExpander { Header = _loc["Settings_Group_" + group.Name], IsExpanded = group.Name == "Appearance" };
            var value = group.GetValue(_settings.Current)!;
            if (group.Name == "Sync")
            {
                var link = Button("Settings_OpenVariables", () => App.Current.MainWindow?.NavigateTo("variables"));
                expander.Items.Add(new SettingsCard { Header = _loc["Settings_SyncLink"], Content = link });
                AddProperty(expander, group, value.GetType().GetProperty(nameof(SyncSettings.MaxClients))!);
            }
            else
            {
                foreach (var property in value.GetType().GetProperties())
                {
                    AddProperty(expander, group, property);
                }
            }

            if (group.Name == "Tools")
            {
                expander.Items.Add(new SettingsCard { Header = _loc["Settings_ServerVersion"], Content = new TextBlock { Text = ScrcpyServerInfo.Version } });
            }

            expander.Items.Add(new SettingsCard
            {
                Header = _loc["Settings_ResetDescription"],
                Content = Button("Settings_Reset", async () =>
                {
                    _settings.Update(s => group.SetValue(s, Activator.CreateInstance(group.PropertyType)));
                    if (group.Name == "Sync" && !App.IsSelfCheck)
                    {
                        await App.Current.Services.GetRequiredService<VariableSyncService>().SetEnabledAsync(false);
                    }
                    CheckLanguage();
                    BuildGroups();
                }),
            });
            SettingsHost.Children.Add(expander);
        }

        BuildAbout();
    }

    private void AddProperty(SettingsExpander expander, PropertyInfo group, PropertyInfo property)
    {
        object Value() => property.GetValue(group.GetValue(_settings.Current))!;
        void Save(object v)
        {
            _settings.Update(s => property.SetValue(group.GetValue(s), v));
            CheckLanguage();
            if (group.Name == "Sync" && !App.IsSelfCheck)
            {
                _ = App.Current.Services.GetRequiredService<VariableSyncService>().RestartIfRunningAsync();
            }
        }

        var card = new SettingsCard { Header = _loc["Settings_" + group.Name + "_" + property.Name] };
        var type = property.PropertyType;
        if (type == typeof(bool))
        {
            var toggle = new ToggleSwitch { IsOn = (bool)Value(), OnContent = _loc["Settings_On"], OffContent = _loc["Settings_Off"] };
            toggle.Toggled += (_, _) => Save(toggle.IsOn);
            card.Content = toggle;
        }
        else if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().ToArray();
            var combo = new ComboBox { MinWidth = 180, ItemsSource = values.Select(v => _loc["Settings_Enum_" + type.Name + "_" + v]).ToArray(), SelectedIndex = Array.IndexOf(values, Value()) };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) Save(values[combo.SelectedIndex]); };
            card.Content = combo;
        }
        else if (type == typeof(int) || type == typeof(double))
        {
            var (min, max) = Range(property.Name);
            var box = new NumberBox { Width = 180, Value = Convert.ToDouble(Value(), System.Globalization.CultureInfo.InvariantCulture), Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            box.ValueChanged += (_, _) =>
            {
                if (double.IsFinite(box.Value))
                {
                    object v = type == typeof(int) ? (object)(int)Math.Round(box.Value) : box.Value;
                    Save(v);
                }
            };
            card.Content = box;
        }
        else
        {
            var box = new TextBox { Width = 300, Text = (string)Value(), PlaceholderText = _loc["Settings_BundledAdb"] };
            box.TextChanged += (_, _) => Save(box.Text);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(box);
            row.Children.Add(Button("Settings_Browse", async () =>
            {
                var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (file is not null) box.Text = file.Path;
            }));
            card.Content = row;
            card.Description = _loc["Settings_AdbDescription"];
        }

        expander.Items.Add(card);
    }

    private static (double Min, double Max) Range(string name) => name switch
    {
        "MaxSize" => (0, 4096), "VideoBitRateMbps" => (1, 64), "MaxFps" => (5, 120),
        "DefaultCheckIntervalMs" => (30, 60000), "DefaultPositionJitterDp" => (0, 50),
        "DefaultDelayJitterPercent" or "DefaultDurationJitterPercent" => (0, 100),
        "SwipeThresholdDp" => (1, 100), "LongPressThresholdMs" => (100, 5000),
        "DefaultPlaybackSpeed" => (0.1, 5), "MaxClients" => (1, 256), _ => (0, 65535),
    };

    private void CheckLanguage()
    {
        if (_settings.Current.Appearance.Language != _startupLanguage)
        {
            App.Current.MainWindow?.ShowInfo(_loc["Settings_RestartRequired"]);
        }
    }

    private Button Button(string key, Action action)
    {
        var button = new Button { Content = _loc[key] };
        button.Click += (_, _) => action();
        return button;
    }

    private void BuildAbout()
    {
        var about = new SettingsExpander { Header = _loc["Settings_About"], IsExpanded = true };
        about.Items.Add(new SettingsCard { Header = AppInfo.Name, Description = _loc.Format("Settings_Version", AppInfo.Version), Content = new TextBlock { Text = "AGPL-3.0" } });
        foreach (var (key, url) in new[] { ("Settings_GitHub", AppInfo.RepositoryUrl), ("Settings_Notices", AppInfo.RepositoryUrl + "/blob/csharp-rewrite/THIRD-PARTY-NOTICES.md") })
        {
            about.Items.Add(new SettingsCard { Header = _loc[key], Content = new HyperlinkButton { Content = _loc["Settings_OpenLink"], NavigateUri = new Uri(url) } });
        }
        about.Items.Add(new SettingsCard { Header = _loc["Settings_CheckNow"], Content = Button("Settings_CheckNow", async () =>
        {
            if (App.Current.MainWindow is { } window) await App.Current.Services.GetRequiredService<UpdateService>().CheckAsync(window, true);
        }) });
        var paths = App.Current.Services.GetRequiredService<AppPaths>();
        foreach (var (key, dir) in new[] { ("Settings_OpenLogs", paths.LogsDirectory), ("Settings_OpenData", paths.Root) })
        {
            about.Items.Add(new SettingsCard { Header = _loc[key], Content = Button("Settings_OpenFolder", () => System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"")) });
        }
        SettingsHost.Children.Add(about);
    }
}
