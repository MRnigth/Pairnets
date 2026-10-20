using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Pairnets.Server.Storage;

/// <summary>
/// What an encrypted nest keeps for its computers' keys (docs/ENCRYPTION.md §2.1, §2.6), none of which it can read:
/// <list type="bullet">
/// <item>keys/&lt;computer id&gt;.env: the ring sealed to that computer and signed by the computer that made it;</item>
/// <item>keys/recovery.env: the ring sealed with the recovery key;</item>
/// <item>unlock requests (in memory, 15 minutes): the mailbox two computers pair through. Each message can be written
/// once, so nobody can swap an answer after it was read.</item>
/// </list>
/// The apps check every envelope and message themselves; the nest only stores and passes them on.
/// </summary>
public sealed class KeyStore
{
    public const int MaxEnvelopeBytes = 64 * 1024;
    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxRequests = 20;
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(15);
    private const string RecoveryName = "recovery";
    private const string Suffix = ".env";

    private readonly string _dir;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, KeyRequest> _requests = new(StringComparer.Ordinal);
    private readonly object _write = new();

    public KeyStore(ServerPaths paths, TimeProvider? clock = null)
    {
        _dir = Path.Combine(paths.DataDir, "keys");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>A computer's id in the ring: 16 lower-case hex characters (see ComputerKeys).</summary>
    public static bool IsComputerId(string? id) =>
        id is { Length: 16 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public byte[]? GetEnvelope(string computerId) => IsComputerId(computerId) ? Read(computerId) : null;

    public bool PutEnvelope(string computerId, byte[] envelope)
    {
        if (!IsComputerId(computerId) || envelope.Length is 0 or > MaxEnvelopeBytes)
            return false;
        Write(computerId, envelope);
        return true;
    }

    public bool DeleteEnvelope(string computerId)
    {
        if (!IsComputerId(computerId))
            return false;
        lock (_write)
        {
            var path = Path.Combine(_dir, computerId + Suffix);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
    }

    /// <summary>The computers that have an envelope here.</summary>
    public List<string> Computers()
    {
        if (!Directory.Exists(_dir))
            return [];
        return Directory.EnumerateFiles(_dir, "*" + Suffix)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(IsComputerId)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public byte[]? GetRecovery() => Read(RecoveryName);

    public bool PutRecovery(byte[] envelope)
    {
        if (envelope.Length is 0 or > MaxEnvelopeBytes)
            return false;
        Write(RecoveryName, envelope);
        return true;
    }

    public bool HasRecovery => File.Exists(Path.Combine(_dir, RecoveryName + Suffix));

    private byte[]? Read(string name)
    {
        var path = Path.Combine(_dir, name + Suffix);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private void Write(string name, byte[] data)
    {
        lock (_write)
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, name + Suffix);
            var temp = path + ".new";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
    }

    // ------------------------------------------------------------------ unlock requests

    /// <summary>A new computer asks to be unlocked; null when too many are waiting or the commitment isn't 32 bytes.</summary>
    public KeyRequest? StartRequest(string name, byte[] commitment)
    {
        ExpireRequests();
        if (commitment.Length != 32 || _requests.Count >= MaxRequests)
            return null;
        var request = new KeyRequest(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), name,
            commitment, _clock.GetUtcNow());
        _requests[request.Id] = request;
        return request;
    }

    public KeyRequest? GetRequest(string id)
    {
        ExpireRequests();
        return _requests.TryGetValue(id, out var request) ? request : null;
    }

    public List<KeyRequest> PendingRequests()
    {
        ExpireRequests();
        return _requests.Values.Where(r => r.Ring is null).OrderBy(r => r.Created).ToList();
    }

    public bool EndRequest(string id) => _requests.TryRemove(id, out _);

    /// <summary>Writes one message of a request, once: false when the request is gone or that message is already there.</summary>
    public bool SetMessage(string id, KeyMessage which, byte[] data)
    {
        if (data.Length is 0 or > MaxMessageBytes || GetRequest(id) is not { } request)
            return false;
        lock (request)
        {
            switch (which)
            {
                case KeyMessage.Offer when request.Offer is null:
                    request.Offer = data;
                    return true;
                case KeyMessage.Reveal when request.Offer is not null && request.Reveal is null:
                    request.Reveal = data;
                    return true;
                case KeyMessage.Ring when request.Reveal is not null && request.Ring is null:
                    request.Ring = data;
                    return true;
                default:
                    return false;
            }
        }
    }

    private void ExpireRequests()
    {
        var cutoff = _clock.GetUtcNow() - RequestLifetime;
        foreach (var (id, request) in _requests)
        {
            if (request.Created < cutoff)
                _requests.TryRemove(id, out _);
        }
    }
}

public enum KeyMessage
{
    Offer,
    Reveal,
    Ring,
}

/// <summary>One unlock request: the new computer's commitment, then the offer, the reveal and the sealed ring.</summary>
public sealed class KeyRequest(string id, string name, byte[] commitment, DateTimeOffset created)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public byte[] Commitment { get; } = commitment;
    public DateTimeOffset Created { get; } = created;
    public byte[]? Offer { get; set; }
    public byte[]? Reveal { get; set; }
    public byte[]? Ring { get; set; }
}
