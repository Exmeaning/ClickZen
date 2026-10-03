using ClickZen.Core.Devices;

namespace ClickZen.Device.Adb;

/// <summary>A state transition of one device.</summary>
public sealed record DeviceStateChange(DeviceInfo Device, DeviceAdbState OldState, DeviceAdbState NewState);

/// <summary>Raised by <see cref="DeviceWatcher.DevicesChanged"/>; always carries the full new list.</summary>
public sealed class DeviceListChangedEventArgs : EventArgs
{
    public DeviceListChangedEventArgs(
        IReadOnlyList<DeviceInfo> devices,
        IReadOnlyList<DeviceInfo> added,
        IReadOnlyList<DeviceInfo> removed,
        IReadOnlyList<DeviceStateChange> stateChanged,
        IReadOnlyList<DeviceInfo> updated)
    {
        Devices = devices;
        Added = added;
        Removed = removed;
        StateChanged = stateChanged;
        Updated = updated;
    }

    /// <summary>The complete current list.</summary>
    public IReadOnlyList<DeviceInfo> Devices { get; }

    public IReadOnlyList<DeviceInfo> Added { get; }

    public IReadOnlyList<DeviceInfo> Removed { get; }

    public IReadOnlyList<DeviceStateChange> StateChanged { get; }

    /// <summary>Devices whose properties (model, size, density…) were filled in or refreshed without a state change.</summary>
    public IReadOnlyList<DeviceInfo> Updated { get; }

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && StateChanged.Count == 0 && Updated.Count == 0;
}

/// <summary>Pure list-diff logic used by <see cref="DeviceWatcher"/> (separate for testing).</summary>
internal static class DeviceListDiff
{
    /// <summary>
    /// Merges a fresh adb snapshot (serial + state only) into the current enriched list. Properties of devices
    /// that remain online are kept; a device that changes state loses nothing but its state.
    /// </summary>
    public static (List<DeviceInfo> Next, DeviceListChangedEventArgs Change) Apply(
        IReadOnlyList<DeviceInfo> current, IReadOnlyList<DeviceInfo> snapshot)
    {
        var byserial = current.ToDictionary(d => d.Serial, StringComparer.Ordinal);
        var next = new List<DeviceInfo>(snapshot.Count);
        var added = new List<DeviceInfo>();
        var changed = new List<DeviceStateChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in snapshot)
        {
            if (!seen.Add(s.Serial))
            {
                continue;
            }

            if (byserial.TryGetValue(s.Serial, out var old))
            {
                if (old.State != s.State)
                {
                    var merged = old with { State = s.State };
                    next.Add(merged);
                    changed.Add(new DeviceStateChange(merged, old.State, s.State));
                }
                else
                {
                    next.Add(old);
                }
            }
            else
            {
                next.Add(s);
                added.Add(s);
            }
        }

        var removed = current.Where(d => !seen.Contains(d.Serial)).ToList();
        return (next, new DeviceListChangedEventArgs(next.ToArray(), added, removed, changed, []));
    }

    /// <summary>Replaces <paramref name="enriched"/> in <paramref name="current"/> if it is still present and in the same state.</summary>
    public static (List<DeviceInfo> Next, DeviceInfo? Updated) ApplyEnriched(IReadOnlyList<DeviceInfo> current, DeviceInfo enriched)
    {
        var next = current.ToList();
        var idx = next.FindIndex(d => d.Serial == enriched.Serial);
        if (idx < 0 || next[idx].State != enriched.State || next[idx] == enriched)
        {
            return (next, null);
        }

        next[idx] = enriched;
        return (next, enriched);
    }
}
