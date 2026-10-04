using System.Collections.ObjectModel;
using ClickZen.App.Services;
using ClickZen.App.Services.Logging;
using ClickZen.Core.Persistence;
using ClickZen.Device.Adb;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Serilog.Events;

namespace ClickZen.App.ViewModels;

/// <summary>One line in the terminal transcript.</summary>
public sealed record TerminalLine(string Text, bool IsCommand, bool IsError);

public sealed partial class LogsViewModel : ObservableObject, IDisposable
{
    private const int MaxVisible = 3000;
    private const int MaxTerminalLines = 5000;

    private readonly InMemoryLogSink _sink;
    private readonly DeviceHub _hub;
    private readonly AdbService _adb;
    private readonly AppPaths _paths;
    private readonly ILocalizer _loc;
    private readonly DispatcherQueue _ui;
    private readonly List<string> _history = [];
    private readonly Queue<LogEntry> _pending = new();
    private readonly DispatcherQueueTimer _flush;
    private int _historyIndex = -1;
    private CancellationTokenSource? _running;

    public LogsViewModel(InMemoryLogSink sink, DeviceHub hub, AdbService adb, AppPaths paths, ILocalizer loc)
    {
        _sink = sink;
        _hub = hub;
        _adb = adb;
        _paths = paths;
        _loc = loc;
        _ui = DispatcherQueue.GetForCurrentThread();
        foreach (var e in sink.Snapshot().TakeLast(MaxVisible))
        {
            if (Accept(e))
            {
                Entries.Add(e);
            }
        }

        // Batch log updates (can be hundreds per second while streaming) into one UI update per 200 ms.
        _flush = _ui.CreateTimer();
        _flush.Interval = TimeSpan.FromMilliseconds(200);
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        _sink.EntryAdded += OnEntryAdded;
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public ObservableCollection<TerminalLine> Terminal { get; } = [];

    public IReadOnlyList<string> QuickCommands { get; } =
    [
        "dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'",
        "pm list packages -3",
        "wm size; wm density",
        "getprop ro.build.version.release",
        "dumpsys battery",
        "id",
    ];

    [ObservableProperty]
    public partial int LevelFilter { get; set; } = 1; // 0 debug, 1 info, 2 warning, 3 error

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial string Command { get; set; } = "";

    [ObservableProperty]
    public partial bool AsRoot { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>Raised after new entries were appended (the view scrolls to the end).</summary>
    public event EventHandler? EntriesAppended;

    public event EventHandler? TerminalAppended;

    partial void OnLevelFilterChanged(int value) => Rebuild();

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            Rebuild();
        }
    }

    private void OnEntryAdded(object? sender, LogEntry e)
    {
        lock (_pending)
        {
            _pending.Enqueue(e);
            while (_pending.Count > MaxVisible)
            {
                _pending.Dequeue();
            }
        }
    }

    private void Flush()
    {
        List<LogEntry> batch;
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
        }

        if (IsPaused)
        {
            return;
        }

        var added = false;
        foreach (var e in batch.Where(Accept))
        {
            Entries.Add(e);
            added = true;
        }

        while (Entries.Count > MaxVisible)
        {
            Entries.RemoveAt(0);
        }

        if (added)
        {
            EntriesAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Accept(LogEntry e)
    {
        var min = LevelFilter switch
        {
            0 => LogEventLevel.Debug,
            2 => LogEventLevel.Warning,
            3 => LogEventLevel.Error,
            _ => LogEventLevel.Information,
        };
        if (e.Level < min)
        {
            return false;
        }

        var q = SearchText?.Trim();
        return string.IsNullOrEmpty(q)
               || e.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
               || e.Source.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (e.Device?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var e in _sink.Snapshot().Where(Accept).TakeLast(MaxVisible))
        {
            Entries.Add(e);
        }

        EntriesAppended?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Clear()
    {
        _sink.Clear();
        Entries.Clear();
    }

    [RelayCommand]
    private void OpenLogFolder() => System.Diagnostics.Process.Start("explorer.exe", $"\"{_paths.LogsDirectory}\"");

    // ------------------------------------------------------------------ terminal

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var cmd = Command.Trim();
        if (cmd.Length == 0)
        {
            return;
        }

        var device = _hub.Current;
        if (device is null)
        {
            AppendTerminal(_loc["Logs_NoDevice"], false, true);
            return;
        }

        // Window devices run shell commands on their linked adb device.
        if (device.AdbSerial is not { } serial)
        {
            AppendTerminal(_loc["Window_NoAdbShell"], false, true);
            return;
        }

        RememberHistory(cmd);
        AppendTerminal($"{serial}{(AsRoot ? " #" : " $")} {cmd}", true, false);
        Command = "";
        IsRunning = true;
        _running = new CancellationTokenSource();
        try
        {
            // Streamed so long-running commands (logcat, top -n 1…) show output as it arrives.
            var command = AsRoot ? ShellQuoting.BuildRootCommand(cmd) : cmd;
            await _adb.ExecuteStreamingAsync(serial, command,
                line => _ui.TryEnqueue(() => AppendTerminal(line, false, false)), _running.Token);
        }
        catch (OperationCanceledException)
        {
            AppendTerminal(_loc["Logs_Cancelled"], false, true);
        }
        catch (AdbException ex)
        {
            AppendTerminal(ex.Message, false, true);
        }
        catch (Exception ex)
        {
            AppendTerminal(ex.Message, false, true);
        }
        finally
        {
            IsRunning = false;
            _running.Dispose();
            _running = null;
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _running?.Cancel();

    [RelayCommand]
    private void ClearTerminal() => Terminal.Clear();

    [RelayCommand]
    private void UseQuick(string command)
    {
        Command = command;
        RunCommand.Execute(null);
    }

    /// <summary>Up/down arrow history navigation; returns the text to show.</summary>
    public void HistoryStep(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = _historyIndex < 0
            ? (direction < 0 ? _history.Count - 1 : -1)
            : _historyIndex + direction;
        if (_historyIndex < 0 || _historyIndex >= _history.Count)
        {
            _historyIndex = -1;
            Command = "";
            return;
        }

        Command = _history[_historyIndex];
    }

    private void RememberHistory(string cmd)
    {
        _history.Remove(cmd);
        _history.Add(cmd);
        if (_history.Count > 100)
        {
            _history.RemoveAt(0);
        }

        _historyIndex = -1;
    }

    private void AppendTerminal(string text, bool isCommand, bool isError)
    {
        Terminal.Add(new TerminalLine(text, isCommand, isError));
        while (Terminal.Count > MaxTerminalLines)
        {
            Terminal.RemoveAt(0);
        }

        TerminalAppended?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _flush.Stop();
        _sink.EntryAdded -= OnEntryAdded;
        _running?.Cancel();
    }
}
