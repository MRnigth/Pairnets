using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Pairnets.Server.Auth.WebAuthn;

/// <summary>
/// The random challenges handed to browsers for passkey registration and sign-in. Each is used once, only for
/// the purpose it was made for, and lives five minutes. Kept in memory: a restart just means "try again".
/// </summary>
public sealed class WebAuthnChallenges(TimeProvider? clock = null)
{
    public const string Register = "register";
    public const string SignIn = "signin";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxOutstanding = 200;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Purpose, byte[] Challenge, DateTimeOffset Expires)> _challenges = new(StringComparer.Ordinal);

    /// <summary>A new challenge: its id (sent to the browser and back) and its 32 random bytes.</summary>
    public (string Id, byte[] Challenge) Create(string purpose)
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, value) in _challenges)
        {
            if (value.Expires <= now)
                _challenges.TryRemove(key, out _);
        }
        // An address that keeps asking cannot grow this without limit; the oldest go first, so someone in the middle
        // of signing in is not thrown out by a flood of new requests (which would have to be 200 deep).
        while (_challenges.Count >= MaxOutstanding && _challenges.MinBy(kv => kv.Value.Expires) is { } oldest)
            _challenges.TryRemove(oldest.Key, out _);
        var id = WebAuthnVerifier.ToBase64Url(RandomNumberGenerator.GetBytes(12));
        var challenge = RandomNumberGenerator.GetBytes(32);
        _challenges[id] = (purpose, challenge, now + Lifetime);
        return (id, challenge);
    }

    /// <summary>Takes a challenge out for good (a second try with the same id finds nothing), or null.</summary>
    public byte[]? Consume(string? id, string purpose)
    {
        if (string.IsNullOrEmpty(id) || !_challenges.TryRemove(id, out var entry))
            return null;
        return entry.Purpose == purpose && entry.Expires > _clock.GetUtcNow() ? entry.Challenge : null;
    }
}
