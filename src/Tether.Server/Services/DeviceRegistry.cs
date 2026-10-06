using System.Text.Json;
using Tether.Core;
using Tether.Server.Storage;

namespace Tether.Server.Services;

/// <summary>
/// The computers that use this server: when each was last heard from, whether its push channel is
/// open right now, and which app it runs. The apps draw "this computer ⇄ server ⇄ your other
/// computer" from it. Kept in memory and saved to devices.json (at most every
/// <see cref="SaveInterval"/>) so "last seen" survives a restart. Device names are the ones the
/// apps send in X-Device-Id; nothing here affects syncing.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    /// <summary>More names than this (renamed computers, test setups) drop the longest-unseen one.</summary>
    public const int MaxDevices = 32;

    public const int MaxNameLength = 64;

    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _connections = new(StringComparer.Ordinal);
    private readonly string _file;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Timer _saveTimer;
    private bool _dirty;

    private sealed class Entry(string name)
    {
        public string Name { get; } = name;
        public DateTimeOffset LastSeen { get; set; }
        public string? App { get; set; }
        public int Connections { get; set; }
    }

    private sealed record Saved(string Name, DateTimeOffset LastSeenUtc, string? App);

    public DeviceRegistry(ServerPaths paths, ILogger<DeviceRegistry> log, TimeProvider? clock = null)
    {
        _file = Path.Combine(paths.DataDir, "devices.json");
        _clock = clock ?? TimeProvider.System;
        _log = log;
        Load();
        _saveTimer = new Timer(_ => Save(), null, SaveInterval, SaveInterval);
    }

    /// <summary>Turns an X-Device-Id header value into a device name, or null when there is none.</summary>
    public static string? NameFromHeader(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try
        {
            return CleanName(Uri.UnescapeDataString(raw));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string? CleanName(string? name)
    {
        name = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0)
            return null;
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    private static string? CleanApp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var app = new string(raw.Trim().Where(c => !char.IsControl(c)).ToArray());
        return app.Length > 40 ? app[..40] : app;
    }

    /// <summary>A request from <paramref name="device"/> was accepted.</summary>
    public void Seen(string device, string? app)
    {
        lock (_gate)
            Touch(device, app);
    }

    /// <summary>A push channel opened.</summary>
    public void Connected(string connectionId, string device, string? app)
    {
        lock (_gate)
        {
            if (_connections.ContainsKey(connectionId))
                return;
            _connections[connectionId] = device;
            Touch(device, app).Connections++;
        }
    }

    /// <summary>A push channel closed (the computer went to sleep, quit or lost its connection).</summary>
    public void Disconnected(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out var device))
                return;
            if (_devices.TryGetValue(device, out var entry))
            {
                entry.Connections = Math.Max(0, entry.Connections - 1);
                entry.LastSeen = _clock.GetUtcNow();
                _dirty = true;
            }
        }
    }

    /// <summary>Every known computer, most recently seen first.</summary>
    public List<DeviceInfo> List()
    {
        lock (_gate)
        {
            return _devices.Values
                .OrderByDescending(e => e.Connections > 0)
                .ThenByDescending(e => e.LastSeen)
                .Select(e => new DeviceInfo(e.Name, e.Connections > 0, e.LastSeen, e.App))
                .ToList();
        }
    }

    private Entry Touch(string device, string? app)
    {
        if (!_devices.TryGetValue(device, out var entry))
        {
            if (_devices.Count >= MaxDevices)
            {
                var oldest = _devices.Values.Where(e => e.Connections == 0).OrderBy(e => e.LastSeen).FirstOrDefault()
                             ?? _devices.Values.OrderBy(e => e.LastSeen).First();
                _devices.Remove(oldest.Name);
            }
            entry = new Entry(device);
            _devices[device] = entry;
            _log.LogInformation("First request from computer {Device}", device);
        }
        entry.LastSeen = _clock.GetUtcNow();
        if (CleanApp(app) is { } a)
            entry.App = a;
        _dirty = true;
        return entry;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
                return;
            var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(_file), TetherJson.Options) ?? [];
            foreach (var s in saved.Take(MaxDevices))
            {
                if (CleanName(s.Name) is { } name)
                    _devices[name] = new Entry(name) { LastSeen = s.LastSeenUtc, App = CleanApp(s.App) };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogWarning("Could not read {File}; starting with no known computers: {Error}", _file, ex.Message);
        }
    }

    /// <summary>Writes devices.json if anything changed (atomically: temp file, then rename).</summary>
    public void Save()
    {
        List<Saved> snapshot;
        lock (_gate)
        {
            if (!_dirty)
                return;
            _dirty = false;
            snapshot = _devices.Values.Select(e => new Saved(e.Name, e.LastSeen, e.App)).ToList();
        }
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, TetherJson.Options));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Could not save {File}: {Error}", _file, ex.Message);
            lock (_gate)
                _dirty = true;
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Save();
    }
}
