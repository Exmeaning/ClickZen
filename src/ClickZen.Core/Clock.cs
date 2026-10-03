using System.Diagnostics;

namespace ClickZen.Core;

/// <summary>Monotonic time source. Abstracted so the engine and player can be tested deterministically.</summary>
public interface IClock
{
    /// <summary>Milliseconds since an arbitrary fixed point; never goes backwards.</summary>
    long ElapsedMs { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public long ElapsedMs => _sw.ElapsedMilliseconds;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}

/// <summary>Manual clock for tests: delays complete instantly and advance time.</summary>
public sealed class FakeClock : IClock
{
    private long _now;

    public long ElapsedMs => Interlocked.Read(ref _now);

    public void Advance(long ms) => Interlocked.Add(ref _now, ms);

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (delay > TimeSpan.Zero)
        {
            Advance((long)delay.TotalMilliseconds);
        }

        return Task.CompletedTask;
    }
}
