using System.Collections.ObjectModel;
using System.ComponentModel;
using ClickZen.App.Services;
using ClickZen.Core.Automation;
using ClickZen.Core.Settings;
using ClickZen.Core.Variables;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace ClickZen.App.ViewModels;

/// <summary>Where a variable row's value comes from.</summary>
public enum VariableRowSource
{
    /// <summary>Declared in the open scheme.</summary>
    Declared,
    /// <summary>Only exists at run time (created by an action, a sync client…).</summary>
    Runtime,
}

/// <summary>
/// One row of the variables table: a declaration of the open scheme (editable), or a runtime-only
/// value. <see cref="CurrentValue"/> shows the live value – global store, or the scheme scope of the
/// selected running device.
/// </summary>
public sealed partial class VariableRow : ObservableObject
{
    private readonly VariablesViewModel _owner;
    private bool _loading;

    internal VariableRow(VariablesViewModel owner, VariableDefinition? definition, string name)
    {
        _owner = owner;
        Definition = definition;
        _loading = true;
        Name = name;
        if (definition is not null)
        {
            TypeIndex = (int)definition.Type;
            ScopeIndex = (int)definition.Scope;
            SyncIndex = (int)definition.Sync;
            Initial = definition.Initial.AsString;
            Description = definition.Description;
        }

        _loading = false;
    }

    /// <summary>Null for runtime-only variables.</summary>
    public VariableDefinition? Definition { get; }

    public bool IsDeclared => Definition is not null;

    public VariableRowSource Source => IsDeclared ? VariableRowSource.Declared : VariableRowSource.Runtime;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial int TypeIndex { get; set; }

    [ObservableProperty]
    public partial int ScopeIndex { get; set; }

    [ObservableProperty]
    public partial int SyncIndex { get; set; }

    [ObservableProperty]
    public partial string Initial { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    /// <summary>Live value text ("" when the variable does not exist in the viewed store).</summary>
    [ObservableProperty]
    public partial string CurrentValue { get; set; } = "";

    [ObservableProperty]
    public partial string? NameError { get; set; }

    partial void OnNameChanged(string value)
    {
        if (_loading || Definition is null)
        {
            return;
        }

        NameError = _owner.ValidateName(this, value);
        if (NameError is null)
        {
            Definition.Name = value.Trim();
            _owner.DefinitionsEdited();
        }
    }

    partial void OnTypeIndexChanged(int value)
    {
        if (_loading || Definition is null || value < 0)
        {
            return;
        }

        Definition.Type = (VariableType)value;
        Definition.Initial = Definition.Initial.ConvertTo(Definition.Type);
        _loading = true;
        Initial = Definition.Initial.AsString;
        _loading = false;
        _owner.DefinitionsEdited();
    }

    partial void OnScopeIndexChanged(int value)
    {
        if (_loading || Definition is null || value < 0)
        {
            return;
        }

        Definition.Scope = (VariableScope)value;
        _owner.DefinitionsEdited();
    }

    partial void OnSyncIndexChanged(int value)
    {
        if (_loading || Definition is null || value < 0)
        {
            return;
        }

        Definition.Sync = (SyncDirection)value;
        _owner.DefinitionsEdited();
    }

    partial void OnInitialChanged(string value)
    {
        if (_loading || Definition is null)
        {
            return;
        }

        Definition.Initial = VariableValue.Parse(value ?? "").ConvertTo(Definition.Type);
        _owner.DefinitionsEdited();
    }

    partial void OnDescriptionChanged(string value)
    {
        if (_loading || Definition is null)
        {
            return;
        }

        Definition.Description = value ?? "";
        _owner.DefinitionsEdited();
    }
}

/// <summary>A store the table can show live values from.</summary>
public sealed record VariableView(string Label, VariableStore Store);

/// <summary>
/// Variables page: declarations of the open scheme plus live values, and the sync service card.
/// Values refresh from store change events (throttled to the UI thread).
/// </summary>
public sealed partial class VariablesViewModel : ObservableObject, IDisposable
{
    private readonly AutomationService _automation;
    private readonly VariableSyncService _sync;
    private readonly ILocalizer _loc;
    private readonly SettingsService _settings;
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _refresh;
    private VariableStore? _watched;
    private bool _rebuildQueued;

    public VariablesViewModel(AutomationService automation, VariableSyncService sync, ILocalizer loc, SettingsService settings)
    {
        _automation = automation;
        _settings = settings;
        _sync = sync;
        _loc = loc;
        _ui = DispatcherQueue.GetForCurrentThread();
        _refresh = _ui.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(150);
        _refresh.IsRepeating = false;
        _refresh.Tick += (_, _) => RefreshValues();

        _automation.DocumentReplaced += OnDocumentReplaced;
        _automation.EnginesChanged += OnEnginesChanged;
        _sync.PropertyChanged += OnSyncChanged;
        _sync.Globals.Changed += OnStoreChanged;

        Port = _sync.Port > 0 ? _sync.Port : SyncPortSetting;
        Token = _sync.Token;
        RebuildViews();
        Rebuild();
    }

    public ObservableCollection<VariableRow> Rows { get; } = [];

    public ObservableCollection<VariableView> Views { get; } = [];

    public ObservableCollection<string> Clients { get; } = [];

    public IReadOnlyList<string> LocalAddresses { get; } = VariableSyncService.LocalAddresses();

    [ObservableProperty]
    public partial VariableView? SelectedView { get; set; }

    [ObservableProperty]
    public partial VariableRow? SelectedRow { get; set; }

    public string SchemeName => _automation.DisplayName;

    // ------------------------------------------------------------------ sync card

    public bool SyncEnabled
    {
        get => _sync.IsRunning || SyncEnabledSetting;
        set => _ = SetSyncEnabledAsync(value);
    }

    private bool SyncEnabledSetting => _settings.Current.Sync.Enabled;

    private int SyncPortSetting => _settings.Current.Sync.Port;

    public bool IsRunning => _sync.IsRunning;

    public string? SyncError => _sync.LastError;

    [ObservableProperty]
    public partial double Port { get; set; }

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    public string StatusText => _sync.IsRunning
        ? _loc.Format("Vars_SyncRunning", _sync.Port, _sync.Clients.Count)
        : _sync.LastError is { } e ? _loc.Format("Vars_SyncFailed", e) : _loc["Vars_SyncStopped"];

    public string AddressesText => LocalAddresses.Count == 0
        ? _loc["Vars_NoAddresses"]
        : string.Join("   ", LocalAddresses.Select(a => $"{a}:{(_sync.IsRunning ? _sync.Port : (int)Port)}"));

    private async Task SetSyncEnabledAsync(bool enabled)
    {
        await _sync.SetEnabledAsync(enabled);
        OnPropertyChanged(nameof(SyncEnabled));
    }

    /// <summary>Applies the port and token (restarting the server when it runs).</summary>
    [RelayCommand]
    public async Task ApplySyncSettingsAsync()
    {
        var port = (int)Math.Clamp(double.IsFinite(Port) ? Port : 9527, 1, 65535);
        _settings.Update(s => s.Sync.Port = port);
        _sync.Token = Token ?? "";
        await _sync.RestartIfRunningAsync();
        OnPropertyChanged(nameof(AddressesText));
    }

    [RelayCommand]
    public async Task RegenerateTokenAsync()
    {
        Token = VariableSyncServer.NewToken();
        await ApplySyncSettingsAsync();
    }

    private void OnSyncChanged(object? sender, PropertyChangedEventArgs e) => _ui.TryEnqueue(() =>
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(SyncEnabled));
        OnPropertyChanged(nameof(SyncError));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AddressesText));
        if (e.PropertyName == nameof(VariableSyncService.Clients))
        {
            Clients.Clear();
            foreach (var c in _sync.Clients)
            {
                var subs = c.Subscriptions.Count == 0 ? "-" : string.Join(", ", c.Subscriptions.Order(StringComparer.Ordinal));
                Clients.Add(_loc.Format("Vars_ClientLine", c.Name, c.RemoteEndPoint?.ToString() ?? "?", c.ConnectedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture), subs));
            }
        }
    });

    // ------------------------------------------------------------------ declarations

    [RelayCommand]
    public void AddVariable()
    {
        var name = UniqueName("var");
        var def = new VariableDefinition { Name = name, Type = VariableType.Int, Initial = VariableValue.Zero };
        _automation.Scheme.Variables.Add(def);
        DefinitionsEdited();
        Rebuild();
        SelectedRow = Rows.FirstOrDefault(r => r.Definition == def);
    }

    /// <summary>Turns a runtime-only variable into a declaration (keeps its current type and value).</summary>
    [RelayCommand]
    public void Declare(VariableRow? row)
    {
        if (row is null || row.IsDeclared)
        {
            return;
        }

        var value = (SelectedView?.Store ?? _sync.Globals).Get(row.Name);
        _automation.Scheme.Variables.Add(new VariableDefinition { Name = row.Name, Type = value.Type, Initial = value });
        DefinitionsEdited();
        Rebuild();
    }

    [RelayCommand]
    public void Remove(VariableRow? row)
    {
        if (row?.Definition is not { } def)
        {
            return;
        }

        _automation.Scheme.Variables.Remove(def);
        DefinitionsEdited();
        Rebuild();
    }

    /// <summary>Sets the live value in the viewed store (debugging while a scheme runs).</summary>
    public string? SetValue(VariableRow row, string text)
    {
        var store = SelectedView?.Store ?? _sync.Globals;
        try
        {
            store.Set(row.Name, VariableValue.Parse(text ?? ""), VariableChangeSource.User);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    internal string? ValidateName(VariableRow row, string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0)
        {
            return _loc["Vars_NameEmpty"];
        }

        if (!IsIdentifier(name))
        {
            return _loc["Vars_NameInvalid"];
        }

        return _automation.Scheme.Variables.Any(d => d != row.Definition && string.Equals(d.Name, name, StringComparison.Ordinal))
            ? _loc["Vars_NameDuplicate"]
            : null;
    }

    /// <summary>Same rule as the expression parser: letter or underscore, then letters, digits, underscores or dots.</summary>
    public static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

    internal void DefinitionsEdited()
    {
        _automation.VariablesEdited();
        OnPropertyChanged(nameof(SchemeName));
    }

    private string UniqueName(string stem)
    {
        for (var i = 1; ; i++)
        {
            var n = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_automation.Scheme.Variables.All(d => d.Name != n))
            {
                return n;
            }
        }
    }

    // ------------------------------------------------------------------ rows and live values

    partial void OnSelectedViewChanged(VariableView? value)
    {
        if (_watched is not null && _watched != _sync.Globals)
        {
            _watched.Changed -= OnStoreChanged;
        }

        _watched = value?.Store;
        if (_watched is not null && _watched != _sync.Globals)
        {
            _watched.Changed += OnStoreChanged;
        }

        Rebuild();
    }

    private void OnDocumentReplaced(object? sender, EventArgs e) => _ui.TryEnqueue(() =>
    {
        OnPropertyChanged(nameof(SchemeName));
        Rebuild();
    });

    private void OnEnginesChanged(object? sender, EventArgs e) => _ui.TryEnqueue(RebuildViews);

    private void OnStoreChanged(object? sender, VariableChange e)
    {
        // Values change often while a scheme runs: coalesce into one refresh; new names need a rebuild.
        _ui.TryEnqueue(() =>
        {
            if (!Rows.Any(r => r.Name == e.Name))
            {
                if (!_rebuildQueued)
                {
                    _rebuildQueued = true;
                    _ui.TryEnqueue(() =>
                    {
                        _rebuildQueued = false;
                        Rebuild();
                    });
                }

                return;
            }

            if (!_refresh.IsRunning)
            {
                _refresh.Start();
            }
        });
    }

    private void RebuildViews()
    {
        var selected = SelectedView;
        Views.Clear();
        Views.Add(new VariableView(_loc["Vars_ViewGlobal"], _sync.Globals));
        foreach (var (device, engine) in _automation.RunningEngines)
        {
            var label = string.IsNullOrWhiteSpace(device.Info.DisplayName) ? device.Serial : device.Info.DisplayName;
            Views.Add(new VariableView(_loc.Format("Vars_ViewDevice", label), engine.Variables));
        }

        SelectedView = Views.FirstOrDefault(v => selected is not null && v.Store == selected.Store) ?? Views[0];
    }

    private void Rebuild()
    {
        var selectedName = SelectedRow?.Name;
        Rows.Clear();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in _automation.Scheme.Variables)
        {
            declared.Add(def.Name);
            Rows.Add(new VariableRow(this, def, def.Name));
        }

        foreach (var name in AllNames(SelectedView?.Store ?? _sync.Globals).Where(n => !declared.Contains(n)))
        {
            Rows.Add(new VariableRow(this, null, name));
        }

        RefreshValues();
        SelectedRow = Rows.FirstOrDefault(r => r.Name == selectedName);
    }

    private static IEnumerable<string> AllNames(VariableStore store)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        for (var s = store; s is not null; s = s.Parent)
        {
            foreach (var (n, _) in s.Snapshot())
            {
                names.Add(n);
            }
        }

        return names;
    }

    private void RefreshValues()
    {
        var store = SelectedView?.Store ?? _sync.Globals;
        foreach (var row in Rows)
        {
            row.CurrentValue = store.TryGet(row.Name, out var v) ? Format(v) : "";
        }
    }

    private static string Format(VariableValue v) => v.Type == VariableType.String ? "\"" + v.AsString + "\"" : v.AsString;

    public void Dispose()
    {
        _automation.DocumentReplaced -= OnDocumentReplaced;
        _automation.EnginesChanged -= OnEnginesChanged;
        _sync.PropertyChanged -= OnSyncChanged;
        _sync.Globals.Changed -= OnStoreChanged;
        if (_watched is not null && _watched != _sync.Globals)
        {
            _watched.Changed -= OnStoreChanged;
        }

        _refresh.Stop();
    }
}
