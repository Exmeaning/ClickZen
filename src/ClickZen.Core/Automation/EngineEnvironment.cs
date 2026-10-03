using ClickZen.Core.Geometry;
using ClickZen.Core.Input;
using ClickZen.Core.Recording;
using ClickZen.Core.Variables;
using Microsoft.Extensions.Logging;

namespace ClickZen.Core.Automation;

/// <summary>Runs shell commands on the device (implemented by the Device layer).</summary>
public interface IDeviceShell
{
    Task<string> ShellAsync(string command, bool asRoot, CancellationToken ct);
}

/// <summary>Everything the engine needs from the outside world. One instance per device run.</summary>
public sealed class EngineEnvironment
{
    public required IFrameSource Frames { get; init; }
    public required ITouchInjector Input { get; init; }
    public required IImageMatcher Matcher { get; init; }

    /// <summary>Current device screen size in its current orientation (read every round).</summary>
    public required Func<SizeI> ScreenSize { get; init; }

    public double DpScale { get; init; } = 1;
    public IDeviceShell? Shell { get; init; }

    /// <summary>Resolves <see cref="PlayRecordingAction.Recording"/> to a document.</summary>
    public Func<string, RecordingDocument?>? ResolveRecording { get; init; }

    /// <summary>Store shared by all devices (global-scope variables). Null = private.</summary>
    public VariableStore? GlobalVariables { get; init; }

    public IClock Clock { get; init; } = SystemClock.Instance;
    public Random Random { get; init; } = Random.Shared;
    public ILogger? Logger { get; init; }

    /// <summary>How long to wait for a new frame after an input action before using the latest one.</summary>
    public TimeSpan FreshFrameTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Label used in logs (device serial/name).</summary>
    public string DeviceLabel { get; init; } = "";
}

public enum EngineState
{
    Idle,
    Running,
    Stopped,
    Faulted,
}

/// <summary>Mutable per-task bookkeeping, readable by the UI.</summary>
public sealed class TaskRuntime
{
    public long? LastRunEndMs { get; internal set; }
    public int RunCount { get; internal set; }
    public bool Disabled { get; internal set; }
    public string? LastRule { get; internal set; }
}

public sealed record EngineLogEntry(long AtMs, LogLevel Level, string Message, string? TaskId);

public sealed record TaskFiredEventArgs(string TaskId, string TaskName, string RuleName, MatchResult? Match);
