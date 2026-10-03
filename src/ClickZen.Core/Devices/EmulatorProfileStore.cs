using System.Text.Json;
using System.Text.Json.Serialization;
using ClickZen.Core.Geometry;
using ClickZen.Core.Persistence;
using ClickZen.Core.Recording;
using Microsoft.Extensions.Logging;

namespace ClickZen.Core.Devices;

/// <summary>
/// Loads/saves <see cref="EmulatorProfilesDocument"/> (emulator-profiles.json, formatVersion 1) atomically.
/// Sizes are written as [w, h] and rectangles as [x, y, w, h]. A corrupt file is moved aside
/// (*.corrupt-yyyyMMddHHmmss) and an empty list is returned; a file from a newer format version is
/// read best-effort but never overwritten silently (<see cref="Save"/> throws).
/// Thread-safe; returned profiles are copies.
/// </summary>
public sealed class EmulatorProfileStore
{
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private List<EmulatorProfile>? _cache;
    private bool _readOnly;

    public EmulatorProfileStore(AppPaths paths, ILogger<EmulatorProfileStore>? logger = null)
        : this(paths.EmulatorProfilesFile, logger)
    {
    }

    public EmulatorProfileStore(string filePath, ILogger? logger = null)
    {
        FilePath = filePath;
        _logger = logger;
    }

    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public string FilePath { get; }

    /// <summary>Raised after a successful save.</summary>
    public event EventHandler? Changed;

    /// <summary>All profiles (copies), in file order.</summary>
    public IReadOnlyList<EmulatorProfile> GetAll()
    {
        lock (_gate)
        {
            return EnsureLoaded().Select(p => p.Clone()).ToList();
        }
    }

    public EmulatorProfile? Get(string id)
    {
        lock (_gate)
        {
            return EnsureLoaded().FirstOrDefault(p => p.Id == id)?.Clone();
        }
    }

    /// <summary>Inserts or replaces (by <see cref="EmulatorProfile.Id"/>) and saves. Updates <see cref="EmulatorProfile.UpdatedAt"/>.</summary>
    public void Upsert(EmulatorProfile profile)
    {
        var copy = profile.Clone();
        if (string.IsNullOrWhiteSpace(copy.Id))
        {
            copy.Id = EmulatorProfile.NewId();
            profile.Id = copy.Id;
        }

        copy.UpdatedAt = DateTimeOffset.Now;
        lock (_gate)
        {
            var list = EnsureLoaded();
            var i = list.FindIndex(p => p.Id == copy.Id);
            if (i >= 0)
            {
                list[i] = copy;
            }
            else
            {
                list.Add(copy);
            }

            SaveLocked(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            var list = EnsureLoaded();
            removed = list.RemoveAll(p => p.Id == id) > 0;
            if (removed)
            {
                SaveLocked(list);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>Replaces the whole list and saves.</summary>
    public void Save(IEnumerable<EmulatorProfile> profiles)
    {
        lock (_gate)
        {
            var list = profiles.Select(p => p.Clone()).ToList();
            SaveLocked(list);
            _cache = list;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the in-memory copy so the next read goes to disk.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    // ------------------------------------------------------------------ static helpers (tests, import/export)

    public static string Serialize(EmulatorProfilesDocument document) => JsonSerializer.Serialize(document, JsonOptions);

    public static EmulatorProfilesDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<EmulatorProfilesDocument>(json, JsonOptions) ?? new EmulatorProfilesDocument();

    private List<EmulatorProfile> EnsureLoaded()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        _cache = [];
        _readOnly = false;
        if (!File.Exists(FilePath))
        {
            return _cache;
        }

        try
        {
            var doc = Deserialize(File.ReadAllText(FilePath));
            if (doc.FormatVersion > EmulatorProfilesDocument.CurrentFormatVersion)
            {
                _readOnly = true;
                _logger?.LogWarning("{Path} has format version {Version} (newer than {Supported}); opened read-only",
                    FilePath, doc.FormatVersion, EmulatorProfilesDocument.CurrentFormatVersion);
            }

            _cache = doc.Profiles.Where(p => p is not null).ToList();
            foreach (var p in _cache)
            {
                p.Match ??= new WindowMatchRule();
                if (string.IsNullOrWhiteSpace(p.Id))
                {
                    p.Id = EmulatorProfile.NewId();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            var backup = $"{FilePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(FilePath, backup, overwrite: true); } catch (IOException) { }
            _logger?.LogWarning(ex, "{Path} was unreadable; moved to {Backup}", FilePath, backup);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Could not read {Path}", FilePath);
        }

        return _cache;
    }

    private void SaveLocked(List<EmulatorProfile> list)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException($"{FilePath} was written by a newer ClickZen and is read-only.");
        }

        var doc = new EmulatorProfilesDocument { Profiles = list };
        AtomicFile.WriteAllText(FilePath, Serialize(doc));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonDefaults.Options);
        o.Converters.Add(new SizeIConverter());
        o.Converters.Add(new RectIArrayConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

/// <summary>Encodes <see cref="RectI"/> as [x, y, width, height].</summary>
public sealed class RectIArrayConverter : JsonConverter<RectI>
{
    public override RectI Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected [x, y, width, height].");
        }

        Span<int> v = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            reader.Read();
            v[i] = reader.GetInt32();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("Expected end of [x, y, width, height].");
        }

        return new RectI(v[0], v[1], v[2], v[3]);
    }

    public override void Write(Utf8JsonWriter writer, RectI value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Width);
        writer.WriteNumberValue(value.Height);
        writer.WriteEndArray();
    }
}
