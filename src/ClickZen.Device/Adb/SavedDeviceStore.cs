using ClickZen.Core.Devices;
using ClickZen.Core.Persistence;
using ClickZen.Device.Adb.Parsing;
using Microsoft.Extensions.Logging;

namespace ClickZen.Device.Adb;

/// <summary>
/// The user's remembered wireless devices (<see cref="AppPaths.DevicesFile"/>). Thread-safe; every mutation is
/// persisted atomically and then raises <see cref="Changed"/> (on the calling thread, outside the lock).
/// </summary>
public sealed class SavedDeviceStore
{
    private readonly JsonFileStore<DevicesDocument> _store;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private DevicesDocument _doc;

    public SavedDeviceStore(AppPaths paths, ILogger<SavedDeviceStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _store = new JsonFileStore<DevicesDocument>(paths.DevicesFile, logger);
        _doc = Normalize(_store.Load());
    }

    /// <summary>Raised after the saved list changed and was written to disk.</summary>
    public event EventHandler? Changed;

    public string FilePath => _store.FilePath;

    /// <summary>Copies of all saved devices (mutating them does not affect the store).</summary>
    public IReadOnlyList<SavedDevice> GetAll()
    {
        lock (_gate)
        {
            return _doc.Saved.Select(Clone).ToArray();
        }
    }

    /// <summary>Finds a saved device by <c>host:port</c> (host compared case-insensitively).</summary>
    public SavedDevice? Find(string address)
    {
        if (!HostPortParser.TryParse(address, HostPortParser.DefaultAdbPort, out var host, out var port))
        {
            return null;
        }

        lock (_gate)
        {
            var hit = _doc.Saved.FirstOrDefault(d => Matches(d, host, port));
            return hit is null ? null : Clone(hit);
        }
    }

    /// <summary>
    /// Adds <paramref name="device"/>, or – when one with the same host and port exists – updates its name and
    /// <see cref="SavedDevice.AutoConnect"/>. Returns <see langword="true"/> if a new entry was added.
    /// </summary>
    public bool AddOrUpdate(SavedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var host = device.Host.Trim();
        if (host.Length == 0)
        {
            throw new ArgumentException("Host must not be empty.", nameof(device));
        }

        if (device.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(device), device.Port, "Port must be 1-65535.");
        }

        bool added;
        lock (_gate)
        {
            var existing = _doc.Saved.FirstOrDefault(d => Matches(d, host, device.Port));
            if (existing is null)
            {
                var copy = Clone(device);
                copy.Host = host;
                copy.Name = device.Name.Trim();
                _doc.Saved.Add(copy);
                added = true;
            }
            else
            {
                existing.Name = device.Name.Trim();
                existing.AutoConnect = device.AutoConnect;
                added = false;
            }

            Persist();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>Removes the device with this <c>host:port</c>. Returns <see langword="false"/> if it was not saved.</summary>
    public bool Remove(string address)
    {
        if (!HostPortParser.TryParse(address, HostPortParser.DefaultAdbPort, out var host, out var port))
        {
            return false;
        }

        lock (_gate)
        {
            if (_doc.Saved.RemoveAll(d => Matches(d, host, port)) == 0)
            {
                return false;
            }

            Persist();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Persist()
    {
        try
        {
            _store.Save(_doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the in-memory change; the next successful save will include it.
            _logger?.LogError(ex, "Failed to save {Path}", _store.FilePath);
        }
    }

    private static bool Matches(SavedDevice d, string host, int port) =>
        d.Port == port && string.Equals(d.Host.Trim().Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    private static SavedDevice Clone(SavedDevice d) => new()
    {
        Name = d.Name,
        Host = d.Host,
        Port = d.Port,
        AutoConnect = d.AutoConnect,
    };

    /// <summary>Drops invalid and duplicate entries from a hand-edited file.</summary>
    private static DevicesDocument Normalize(DevicesDocument doc)
    {
        doc.Saved ??= [];
        var clean = new List<SavedDevice>();
        foreach (var d in doc.Saved)
        {
            if (d is null || string.IsNullOrWhiteSpace(d.Host) || d.Port is < 1 or > 65535)
            {
                continue;
            }

            d.Host = d.Host.Trim();
            d.Name ??= "";
            if (!clean.Any(c => Matches(c, d.Host, d.Port)))
            {
                clean.Add(d);
            }
        }

        doc.Saved = clean;
        return doc;
    }
}
