using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClickZen.Core.Persistence;

/// <summary>
/// Loads and saves a single strongly-typed JSON document atomically.
/// A corrupt file never crashes the app: it is moved aside (*.corrupt-yyyyMMddHHmmss) and defaults are used.
/// </summary>
public sealed class JsonFileStore<T> where T : class, new()
{
    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();

    public JsonFileStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger;
    }

    public string FilePath => _path;

    public T Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new T();
            }

            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? new T();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                try { File.Move(_path, backup, overwrite: true); } catch (IOException) { }
                _logger?.LogWarning(ex, "Settings file {Path} was unreadable; moved to {Backup} and using defaults", _path, backup);
                return new T();
            }
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
            AtomicFile.WriteAllBytes(_path, bytes);
        }
    }
}
