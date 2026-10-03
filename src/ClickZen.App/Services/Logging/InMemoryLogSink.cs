using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace ClickZen.App.Services.Logging;

/// <summary>One log line as shown in the in-app log page.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogEventLevel Level, string Source, string? Device, string Message, string? Exception);

/// <summary>
/// Serilog sink keeping the most recent entries in memory for the Logs page and status bar.
/// Thread-safe; consumers subscribe to <see cref="EntryAdded"/> and marshal to the UI thread themselves.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;
    private int _count;

    public InMemoryLogSink(int capacity = 5000) => _capacity = capacity;

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var source = logEvent.Properties.TryGetValue("SourceContext", out var sc) ? Shorten(sc.ToString().Trim('"')) : "";
        var device = logEvent.Properties.TryGetValue("Device", out var dv) ? dv.ToString().Trim('"') : null;
        var entry = new LogEntry(logEvent.Timestamp, logEvent.Level, source, device,
            logEvent.RenderMessage(System.Globalization.CultureInfo.CurrentCulture), logEvent.Exception?.ToString());

        _entries.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > _capacity && _entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
        }

        EntryAdded?.Invoke(this, entry);
    }

    private static string Shorten(string sourceContext)
    {
        var dot = sourceContext.LastIndexOf('.');
        return dot >= 0 ? sourceContext[(dot + 1)..] : sourceContext;
    }
}
