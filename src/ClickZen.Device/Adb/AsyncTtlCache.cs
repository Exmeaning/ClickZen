using System.Collections.Concurrent;

namespace ClickZen.Device.Adb;

/// <summary>
/// Small async cache with per-entry time-to-live and in-flight de-duplication: concurrent callers for the same
/// key share one factory invocation. Failed or cancelled loads are not cached.
/// </summary>
internal sealed class AsyncTtlCache<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries;
    private readonly TimeProvider _time;

    public AsyncTtlCache(TimeProvider? time = null, IEqualityComparer<TKey>? comparer = null)
    {
        _time = time ?? TimeProvider.System;
        _entries = new ConcurrentDictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Returns the cached value for <paramref name="key"/> if it is younger than <paramref name="ttl"/>,
    /// otherwise starts (or joins) a load. The factory must not depend on any single caller's cancellation token.
    /// </summary>
    public Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(factory);
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                var task = existing.Task;
                var failed = task.IsFaulted || task.IsCanceled;
                var expired = task.IsCompletedSuccessfully && _time.GetElapsedTime(existing.CreatedAt) > ttl;
                if (!failed && !expired)
                {
                    return task;
                }

                _entries.TryRemove(KeyValuePair.Create(key, existing));
                continue;
            }

            var tcs = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new Entry(tcs.Task, _time.GetTimestamp());
            if (!_entries.TryAdd(key, entry))
            {
                continue;
            }

            _ = LoadAsync(key, entry, tcs, factory);
            return tcs.Task;
        }
    }

    public bool TryGetCompleted(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var e) && e.Task.IsCompletedSuccessfully)
        {
            value = e.Task.Result;
            return true;
        }

        value = default!;
        return false;
    }

    public void Invalidate(TKey key) => _entries.TryRemove(key, out _);

    public void Clear() => _entries.Clear();

    private async Task LoadAsync(TKey key, Entry entry, TaskCompletionSource<TValue> tcs, Func<TKey, Task<TValue>> factory)
    {
        try
        {
            tcs.TrySetResult(await factory(key));
        }
        catch (OperationCanceledException oce)
        {
            _entries.TryRemove(KeyValuePair.Create(key, entry));
            tcs.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            _entries.TryRemove(KeyValuePair.Create(key, entry));
            tcs.TrySetException(ex);
        }
    }

    private sealed record Entry(Task<TValue> Task, long CreatedAt);
}
