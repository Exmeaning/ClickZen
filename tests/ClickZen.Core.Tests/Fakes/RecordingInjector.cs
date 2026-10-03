using ClickZen.Core.Geometry;
using ClickZen.Core.Input;

namespace ClickZen.Core.Tests.Fakes;

/// <summary>Records every injected action together with the fake clock time it happened at.</summary>
public sealed class RecordingInjector : ITouchInjector
{
    private readonly IClock _clock;

    public RecordingInjector(IClock clock) => _clock = clock;

    public sealed record Call(string Kind, long AtMs, PointI? Point = null, int DurationMs = 0,
        IReadOnlyList<IReadOnlyList<TimedPoint>>? Strokes = null, int KeyCode = 0, string? Text = null);

    public List<Call> Calls { get; } = [];

    public string Name => "fake";

    public bool SupportsMultiTouch => true;

    public Task TapAsync(PointI point, int durationMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(new Call("tap", _clock.ElapsedMs, point, durationMs));
        return Task.CompletedTask;
    }

    public Task StrokeAsync(IReadOnlyList<TimedPoint> path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(new Call("stroke", _clock.ElapsedMs, path[0].Position.Round(), path[^1].OffsetMs, [path]));
        return Task.CompletedTask;
    }

    public Task MultiStrokeAsync(IReadOnlyList<IReadOnlyList<TimedPoint>> fingers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(new Call("multi", _clock.ElapsedMs, Strokes: fingers));
        return Task.CompletedTask;
    }

    public Task KeyAsync(int keyCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(new Call("key", _clock.ElapsedMs, KeyCode: keyCode));
        return Task.CompletedTask;
    }

    public Task TextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(new Call("text", _clock.ElapsedMs, Text: text));
        return Task.CompletedTask;
    }
}
