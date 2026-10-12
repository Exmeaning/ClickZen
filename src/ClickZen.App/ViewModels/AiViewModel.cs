using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using ClickZen.App.Services;
using ClickZen.Core.Settings;
using ClickZen.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClickZen.App.ViewModels;

/// <summary>A client the configuration picker offers.</summary>
public sealed record AiClientOption(McpClientKind Kind, string Label);

/// <summary>State of the AI access page: server switch, port, token, capability switches, client snippets, activity.</summary>
public sealed partial class AiViewModel : ObservableObject, IDisposable
{
    private readonly McpService _mcp;
    private readonly SettingsService _settings;
    private readonly ILocalizer _loc;
    private bool _toggling;

    public AiViewModel(McpService mcp, SettingsService settings, ILocalizer loc)
    {
        _mcp = mcp;
        _settings = settings;
        _loc = loc;
        Port = _mcp.Settings.Port;
        AllowRemote = _mcp.Settings.AllowRemote;
        Clients =
        [
            new(McpClientKind.ClaudeCode, loc["Ai_ClientClaudeCode"]),
            new(McpClientKind.Cursor, loc["Ai_ClientCursor"]),
            new(McpClientKind.VsCode, loc["Ai_ClientVsCode"]),
            new(McpClientKind.ClaudeDesktop, loc["Ai_ClientDesktop"]),
            new(McpClientKind.Codex, loc["Ai_ClientCodex"]),
            new(McpClientKind.GenericHttp, loc["Ai_ClientGeneric"]),
        ];
        SelectedClient = Clients[0];
        _mcp.PropertyChanged += OnServicePropertyChanged;
        _mcp.Calls.CollectionChanged += OnCallsChanged;
        RebuildCalls();
    }

    public IReadOnlyList<AiClientOption> Clients { get; }

    /// <summary>Formatted lines of <see cref="McpService.Calls"/>, newest first.</summary>
    public ObservableCollection<string> CallLines { get; } = [];

    public bool HasCalls => CallLines.Count > 0;

    // ------------------------------------------------------------------ server

    public bool ServerEnabled
    {
        get => _mcp.IsRunning || _mcp.Settings.Enabled;
        set
        {
            if (_toggling || value == ServerEnabled)
            {
                return;
            }

            _ = SetEnabledAsync(value);
        }
    }

    private async Task SetEnabledAsync(bool enabled)
    {
        _toggling = true;
        try
        {
            await _mcp.SetEnabledAsync(enabled);
        }
        finally
        {
            _toggling = false;
            RaiseServerState();
        }
    }

    public bool IsRunning => _mcp.IsRunning;

    public string? LastError => _mcp.LastError;

    public string StatusText => _mcp.IsRunning
        ? _loc.Format("Ai_Running", _mcp.LocalUrl)
        : _mcp.LastError is { } e ? _loc.Format("Ai_Failed", e) : _loc["Ai_Stopped"];

    [ObservableProperty]
    public partial double Port { get; set; }

    [ObservableProperty]
    public partial bool AllowRemote { get; set; }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var port = double.IsFinite(Port) ? (int)Math.Clamp(Port, 1024, 65535) : McpSettings.DefaultPort;
        Port = port;
        var allowRemote = AllowRemote;
        var changed = _mcp.Settings.Port != port || _mcp.Settings.AllowRemote != allowRemote;
        _settings.Update(s =>
        {
            s.Mcp.Port = port;
            s.Mcp.AllowRemote = allowRemote;
        });
        if (changed)
        {
            await _mcp.RestartIfRunningAsync();
        }

        RaiseServerState();
    }

    // ------------------------------------------------------------------ token

    public string Token => _mcp.Token;

    [RelayCommand]
    private async Task RegenerateTokenAsync()
    {
        await _mcp.RegenerateTokenAsync();
        OnPropertyChanged(nameof(Token));
        OnPropertyChanged(nameof(ConfigText));
        RaiseServerState();
    }

    // ------------------------------------------------------------------ capabilities

    public bool AllowObserve
    {
        get => _mcp.Allows(McpCapability.Observe);
        set => SetCapability(McpCapability.Observe, value);
    }

    public bool AllowInput
    {
        get => _mcp.Allows(McpCapability.Input);
        set => SetCapability(McpCapability.Input, value);
    }

    public bool AllowAutomation
    {
        get => _mcp.Allows(McpCapability.Automation);
        set => SetCapability(McpCapability.Automation, value);
    }

    public bool AllowRecording
    {
        get => _mcp.Allows(McpCapability.Recording);
        set => SetCapability(McpCapability.Recording, value);
    }

    public bool AllowVariables
    {
        get => _mcp.Allows(McpCapability.Variables);
        set => SetCapability(McpCapability.Variables, value);
    }

    public bool AllowShell
    {
        get => _mcp.Allows(McpCapability.Shell);
        set => SetCapability(McpCapability.Shell, value);
    }

    private void SetCapability(McpCapability capability, bool value)
    {
        if (_mcp.Allows(capability) == value)
        {
            return;
        }

        _mcp.SetAllowed(capability, value);
        OnPropertyChanged("Allow" + capability);
    }

    // ------------------------------------------------------------------ client configuration

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigText), nameof(ConfigHint), nameof(ShowsToken))]
    public partial AiClientOption SelectedClient { get; set; }

    public string ConfigText => McpClientConfig.Build(SelectedClient.Kind, _mcp.LocalUrl, Token, ExePath);

    public string ConfigHint => SelectedClient.Kind switch
    {
        McpClientKind.ClaudeCode => _loc["Ai_HintClaudeCode"],
        _ when McpClientConfig.UsesStdio(SelectedClient.Kind) => _loc["Ai_HintStdio"],
        _ => _loc["Ai_HintHttp"],
    };

    /// <summary>Whether the snippet contains the token (warn before sharing).</summary>
    public bool ShowsToken => !McpClientConfig.UsesStdio(SelectedClient.Kind);

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClickZen.exe");

    // ------------------------------------------------------------------ activity

    [RelayCommand]
    private void ClearCalls() => _mcp.ClearCalls();

    private void OnCallsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildCalls();

    private void RebuildCalls()
    {
        CallLines.Clear();
        foreach (var c in _mcp.Calls.Take(100))
        {
            var mark = c.Success ? "OK " : "ERR";
            var head = _loc.Format("Ai_CallLine", c.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), mark, c.Tool,
                c.ElapsedMs.ToString(CultureInfo.InvariantCulture));
            var detail = string.IsNullOrEmpty(c.Arguments) ? "" : "  (" + c.Arguments + ")";
            var error = c.Success || string.IsNullOrEmpty(c.Error) ? "" : "\n      " + c.Error;
            CallLines.Add(head + detail + "  ·  " + c.Client + error);
        }

        OnPropertyChanged(nameof(HasCalls));
    }

    // ------------------------------------------------------------------ plumbing

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(McpService.IsRunning) or nameof(McpService.LastError) or nameof(McpService.Port) or nameof(McpService.LocalUrl))
        {
            RaiseServerState();
        }
    }

    private void RaiseServerState()
    {
        OnPropertyChanged(nameof(ServerEnabled));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(LastError));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ConfigText));
    }

    public void Dispose()
    {
        _mcp.PropertyChanged -= OnServicePropertyChanged;
        _mcp.Calls.CollectionChanged -= OnCallsChanged;
    }
}
