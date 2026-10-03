using System.Collections.Concurrent;

namespace ClickZen.Core.Variables;

/// <summary>Where a value change came from – lets the sync server avoid echoing a client's own write.</summary>
public enum VariableChangeSource
{
    Engine,
    User,
    Network,
    Reset,
}

public sealed record VariableChange(string Name, VariableValue? OldValue, VariableValue NewValue, VariableChangeSource Source, string? Origin = null);

/// <summary>
/// Thread-safe variable table. Names are case-sensitive. Declared variables have a fixed type:
/// assigning a value of another type converts it (so "count" stays an int even if the network
/// sends "3"). Undeclared variables take the type of whatever is assigned.
/// </summary>
public sealed class VariableStore
{
    private readonly ConcurrentDictionary<string, VariableValue> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VariableType> _declared = new(StringComparer.Ordinal);

    public VariableStore(VariableStore? parent = null) => Parent = parent;

    /// <summary>
    /// Fallback scope (the global store when this is a scheme scope). Reads fall through to the parent
    /// when a name is not found locally; writes go to the scope that already holds the name.
    /// </summary>
    public VariableStore? Parent { get; }

    public event EventHandler<VariableChange>? Changed;

    public void Declare(string name, VariableType type, VariableValue initial)
    {
        _declared[name] = type;
        Set(name, initial, VariableChangeSource.Reset);
    }

    public bool TryGet(string name, out VariableValue value)
    {
        if (_values.TryGetValue(name, out value))
        {
            return true;
        }

        return Parent is not null && Parent.TryGet(name, out value);
    }

    public VariableValue Get(string name, VariableValue fallback = default) => TryGet(name, out var v) ? v : fallback;

    public bool Contains(string name) => _values.ContainsKey(name) || (Parent?.Contains(name) ?? false);

    public void Set(string name, VariableValue value, VariableChangeSource source = VariableChangeSource.Engine, string? origin = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Writes go where the variable lives: locally if present/declared here, else to a parent that has it.
        if (!_values.ContainsKey(name) && !_declared.ContainsKey(name) && Parent is not null && Parent.Contains(name))
        {
            Parent.Set(name, value, source, origin);
            return;
        }

        if (_declared.TryGetValue(name, out var type))
        {
            value = value.ConvertTo(type);
        }

        VariableValue? old = null;
        _values.AddOrUpdate(name, value, (_, prev) =>
        {
            old = prev;
            return value;
        });

        if (old is null || !old.Value.Equals(value) || old.Value.Type != value.Type)
        {
            Changed?.Invoke(this, new VariableChange(name, old, value, source, origin));
        }
    }

    public bool Remove(string name) => _values.TryRemove(name, out _);

    /// <summary>Local (not parent) values, sorted by name.</summary>
    public IReadOnlyList<KeyValuePair<string, VariableValue>> Snapshot() =>
        _values.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();

    public VariableType? DeclaredType(string name) => _declared.TryGetValue(name, out var t) ? t : null;

    /// <summary>Removes all values; declared variables are reset to their declared defaults.</summary>
    public void Clear()
    {
        foreach (var name in _values.Keys.ToArray())
        {
            if (_declared.TryGetValue(name, out var t))
            {
                Set(name, VariableValue.Default(t), VariableChangeSource.Reset);
            }
            else
            {
                _values.TryRemove(name, out _);
            }
        }
    }
}
