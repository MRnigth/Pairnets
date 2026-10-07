using System.Collections.Concurrent;
using System.Text.Json;
using Pairnets.Core;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Services;

/// <summary>
/// The computers that use this server ("Devices" tab in the apps): name, app version and system,
/// when each was first and last seen, whether it is connected right now, and its last change.
/// Kept in memory and saved to devices.json in the data folder at most every 30 seconds.
/// A computer with its own key is listed under "id:&lt;its id&gt;" (see <see cref="Web.DeviceIdentity.RegistryKey"/>),
/// one still on the shared token under the name it sends.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    /// <summary>A device that asked something this recently counts as online, even without a live connection.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    private readonly string _file;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeviceRegistry> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Key, Action? Abort, Func<bool>? StillAllowed)> _connections = new(StringComparer.Ordinal);
    private bool _dirty;
    private DateTimeOffset _lastSave;

    public DeviceRegistry(ServerPaths paths, ILogger<DeviceRegistry> log, TimeProvider? clock = null)
    {
        _file = Path.Combine(paths.DataDir, "devices.json");
        _clock = clock ?? TimeProvider.System;
        _log = log;
        Load();
    }

    private sealed class Entry
    {
        public string? Id { get; set; }

        public string Name { get; set; } = string.Empty;
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public string? AppVersion { get; set; }
        public string? System { get; set; }
        public DateTimeOffset? LastChange { get; set; }
    }

    /// <summary>
    /// Closes the push connections whose computer is no longer allowed in: removed from the command line (another
    /// process), or the shared token turned off. Returns how many were closed.
    /// </summary>
    public int CloseDisallowed()
    {
        var closed = 0;
        foreach (var (connectionId, connection) in _connections)
        {
            if (connection.StillAllowed is { } allowed && !allowed() && _connections.TryRemove(connectionId, out _))
            {
                connection.Abort?.Invoke();
                closed++;
            }
        }
        return closed;
    }

    /// <summary>A computer on the shared token, known only by its name.</summary>
    public void Seen(string device, string? client) => Seen(device, device, client);

    /// <summary>Called for every authenticated request: <paramref name="client"/> is the "X-Tether-Client" header ("1.0.38; Windows").</summary>
    public void Seen(string key, string name, string? client)
    {
        if (!IsName(name))
            return;
        var now = _clock.GetUtcNow();
        var (version, system) = ParseClient(client);
        lock (_gate)
        {
            if (!_devices.TryGetValue(key, out var entry))
            {
                entry = new Entry { Id = IdOf(key), Name = name, FirstSeen = now };
                _devices[key] = entry;
                _dirty = true;
                _lastSave = DateTimeOffset.MinValue; // save a new device right away
            }
            if (entry.Name != name)
            {
                entry.Name = name; // renamed on the nest
                _dirty = true;
            }
            entry.LastSeen = now;
            if (version is not null && version != entry.AppVersion)
            {
                entry.AppVersion = version;
                _dirty = true;
            }
            if (system is not null && system != entry.System)
            {
                entry.System = system;
                _dirty = true;
            }
            if (now - _lastSave >= SaveInterval)
                _dirty = true;
        }
        SaveIfDue();
    }

    /// <summary>The device uploaded, deleted or restored something.</summary>
    public void Changed(string key)
    {
        if (!IsName(key))
            return;
        lock (_gate)
        {
            if (_devices.TryGetValue(key, out var entry))
            {
                entry.LastChange = _clock.GetUtcNow();
                _dirty = true;
            }
        }
        SaveIfDue();
    }

    /// <summary>
    /// A push-channel connection opened; <paramref name="abort"/> closes it when the computer is removed, and
    /// <paramref name="stillAllowed"/> is asked now and then (see <see cref="CloseDisallowed"/>) for removals this process never saw.
    /// </summary>
    public void Connected(string connectionId, string key, Action? abort = null, Func<bool>? stillAllowed = null)
    {
        // Even one that sent no name is tracked: it must still be closable (a computer can leave its name out).
        if (!string.IsNullOrWhiteSpace(key))
            _connections[connectionId] = (key, abort, stillAllowed);
    }

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>A removed computer leaves the list and its push connections are closed.</summary>
    public void Remove(string key)
    {
        lock (_gate)
        {
            if (_devices.Remove(key))
            {
                _dirty = true;
                _lastSave = DateTimeOffset.MinValue;
            }
        }
        foreach (var (connectionId, connection) in _connections)
        {
            if (string.Equals(connection.Key, key, StringComparison.OrdinalIgnoreCase) && _connections.TryRemove(connectionId, out _))
                connection.Abort?.Invoke();
        }
        SaveIfDue();
    }

    /// <summary>A computer that used the shared token as <paramref name="name"/> now has its own key: keep its history.</summary>
    public void Adopt(string name, string newKey, string newName)
    {
        lock (_gate)
        {
            if (!_devices.Remove(name, out var old))
                return;
            old.Id = IdOf(newKey);
            old.Name = newName;
            _devices[newKey] = old;
            _dirty = true;
            _lastSave = DateTimeOffset.MinValue;
        }
        SaveIfDue();
    }

    /// <summary>All devices, most recently seen first.</summary>
    public IReadOnlyList<DeviceInfo> List()
    {
        var now = _clock.GetUtcNow();
        var connected = new HashSet<string>(_connections.Values.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            return _devices
                .OrderByDescending(d => d.Value.LastSeen)
                .Select(d => new DeviceInfo(d.Value.Name, d.Value.FirstSeen, d.Value.LastSeen,
                    connected.Contains(d.Key) || now - d.Value.LastSeen < OnlineWindow,
                    d.Value.AppVersion, d.Value.System, d.Value.LastChange, d.Value.Id))
                .ToList();
        }
    }

    private static string? IdOf(string key) => key.StartsWith("id:", StringComparison.Ordinal) ? key[3..] : null;

    /// <summary>"1.0.38; Windows" → ("1.0.38", "Windows"); anything odd is ignored.</summary>
    internal static (string? Version, string? System) ParseClient(string? client)
    {
        if (string.IsNullOrWhiteSpace(client) || client.Length > 200)
            return (null, null);
        var parts = client.Split(';', 2, StringSplitOptions.TrimEntries);
        var version = parts[0].Length is > 0 and <= 32 ? parts[0] : null;
        var system = parts.Length > 1 && parts[1].Length is > 0 and <= 120 ? parts[1] : null;
        return (version, system);
    }

    private static bool IsName(string device) =>
        !string.IsNullOrWhiteSpace(device) && device != "unknown" && device != "another computer";

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
                return;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file), PairnetsJson.Options) ?? [];
            foreach (var entry in entries.Where(e => IsName(e.Name)))
                _devices[entry.Id is { Length: > 0 } id ? "id:" + id : entry.Name] = entry;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not read {File}: {Error}; the device list starts empty", _file, ex.Message);
        }
    }

    private void SaveIfDue()
    {
        string json;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!_dirty || now - _lastSave < SaveInterval)
                return;
            json = JsonSerializer.Serialize(_devices.Values.ToList(), PairnetsJson.Options);
            _dirty = false;
            _lastSave = now;
        }
        Write(json);
    }

    private void Write(string json)
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save {File}: {Error}", _file, ex.Message);
        }
    }

    public void Dispose()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty && _lastSave != DateTimeOffset.MinValue)
                return;
            json = JsonSerializer.Serialize(_devices.Values.ToList(), PairnetsJson.Options);
            _dirty = false;
        }
        Write(json);
    }
}
