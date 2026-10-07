using System.Collections.Concurrent;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Auth;

/// <summary>
/// Looks up computer keys for every request without a database query each time. Entries live for
/// 30 seconds, so a computer removed from the command line (another process) is locked out within
/// that time; removals through the server itself call <see cref="Forget"/> and take effect at once.
/// Unknown keys are never cached.
/// </summary>
public sealed class DeviceKeys(AuthStore store, TimeProvider? clock = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 1000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (PairedDevice Device, DateTimeOffset Expires)> _byKeyHash = new(StringComparer.Ordinal);
    private volatile CachedFlag? _allowShared;

    private sealed record CachedFlag(bool Value, DateTimeOffset Expires);

    public AuthStore Store => store;

    /// <summary>The computer that owns <paramref name="key"/> (removed ones too), or null for an unknown key.</summary>
    public PairedDevice? Find(string key)
    {
        var hash = Convert.ToBase64String(AuthStore.HashKey(key));
        var now = _clock.GetUtcNow();
        if (_byKeyHash.TryGetValue(hash, out var hit) && hit.Expires > now)
            return hit.Device;
        var device = store.FindByKey(key);
        if (device is null)
        {
            _byKeyHash.TryRemove(hash, out _);
            return null;
        }
        if (_byKeyHash.Count >= MaxEntries)
            _byKeyHash.Clear();
        _byKeyHash[hash] = (device, now + Lifetime);
        return device;
    }

    /// <summary>Drops cached entries of one computer after it was removed or renamed.</summary>
    public void Forget(string deviceId)
    {
        foreach (var (hash, entry) in _byKeyHash)
        {
            if (entry.Device.Id == deviceId)
                _byKeyHash.TryRemove(hash, out _);
        }
    }

    /// <summary>Whether the shared SYNC_TOKEN still works (see <see cref="AuthStore.AllowSharedToken"/>).</summary>
    public bool AllowSharedToken
    {
        get
        {
            var now = _clock.GetUtcNow();
            if (_allowShared is { } cached && cached.Expires > now)
                return cached.Value;
            var value = store.AllowSharedToken;
            _allowShared = new CachedFlag(value, now + Lifetime);
            return value;
        }
        set
        {
            store.AllowSharedToken = value;
            _allowShared = new CachedFlag(value, _clock.GetUtcNow() + Lifetime);
        }
    }
}
