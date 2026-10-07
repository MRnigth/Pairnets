using System.Collections.Concurrent;
using System.Text.Json;
using Pairnets.Core;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Services;

/// <summary>
/// The computers that use this server ("Devices" tab in the apps): name, app version and system,
/// when each was first and last seen, whether it is connected right now, and its last change.
/// Kept in memory and saved to devices.json in the data folder at most every 30 seconds.
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
    private readonly ConcurrentDictionary<string, string> _connections = new(StringComparer.Ordinal);
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
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public string? AppVersion { get; set; }
        public string? System { get; set; }
        public DateTimeOffset? LastChange { get; set; }
    }

    /// <summary>Called for every authenticated request: <paramref name="client"/> is the "X-Pairnets-Client" header ("1.0.38; Windows").</summary>
    public void Seen(string device, string? client)
    {
        if (!IsName(device))
            return;
        var now = _clock.GetUtcNow();
        var (version, system) = ParseClient(client);
        lock (_gate)
        {
            if (!_devices.TryGetValue(device, out var entry))
            {
                entry = new Entry { Name = device, FirstSeen = now };
                _devices[device] = entry;
                _dirty = true;
                _lastSave = DateTimeOffset.MinValue; // save a new device right away
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
    public void Changed(string device)
    {
        if (!IsName(device))
            return;
        lock (_gate)
        {
            if (_devices.TryGetValue(device, out var entry))
            {
                entry.LastChange = _clock.GetUtcNow();
                _dirty = true;
            }
        }
        SaveIfDue();
    }

    public void Connected(string connectionId, string device)
    {
        if (IsName(device))
            _connections[connectionId] = device;
    }

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>All devices, most recently seen first.</summary>
    public IReadOnlyList<DeviceInfo> List()
    {
        var now = _clock.GetUtcNow();
        var connected = new HashSet<string>(_connections.Values, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            return _devices.Values
                .OrderByDescending(e => e.LastSeen)
                .Select(e => new DeviceInfo(e.Name, e.FirstSeen, e.LastSeen,
                    connected.Contains(e.Name) || now - e.LastSeen < OnlineWindow,
                    e.AppVersion, e.System, e.LastChange))
                .ToList();
        }
    }

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
                _devices[entry.Name] = entry;
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
