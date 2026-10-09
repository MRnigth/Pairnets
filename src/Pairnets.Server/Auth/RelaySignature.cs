using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pairnets.Server.Auth;

/// <summary>
/// Checks the Pairnets service's signed calls to a nest in relay mode (cloud/RELAY.md §4, "ra1"). The relay also
/// carries the apps' requests, so the service signs its own: the nest id, the time, a one-time nonce, the method,
/// path and a hash of the body, with HMAC-SHA256 and the 32-byte nest key that only the service and this nest know.
/// The checks run in the order RELAY.md gives: the nest, the signature (constant time), the time (within 120 s), the
/// nonce (not seen in the last 10 minutes). A nonce is only remembered once everything else passed, so nobody without
/// the key can fill the memory. Neither the key nor a signature or nonce is ever logged.
/// </summary>
public sealed class RelaySignature
{
    public const string NestHeader = "X-Pairnets-Nest";
    public const string TimeHeader = "X-Pairnets-Ts";
    public const string NonceHeader = "X-Pairnets-Nonce";
    public const string SignatureHeader = "X-Pairnets-Sig";

    /// <summary>The first line of every signed message: what it is for, so it can never pass as another signature.</summary>
    public const string Purpose = "ra1";

    /// <summary>How far the caller's clock may be from the nest's.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(120);

    /// <summary>How long a nonce is remembered, and how many at most (the oldest go first).</summary>
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(10);

    public const int MaxNonces = 10_000;

    public enum Outcome
    {
        Ok,
        WrongNest,
        BadSignature,
        Stale,
        Replayed,
    }

    private readonly string? _nestId;
    private readonly byte[]? _key;
    private readonly TimeProvider _clock;
    private readonly NonceMemory _nonces = new(NonceLifetime, MaxNonces);

    public RelaySignature(SyncOptions options, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        if (options.RelayMode && StrictBase64Url.TryDecode(options.RelayKey, out var key) && key.Length == 32)
        {
            _nestId = options.RelayNestId;
            _key = key;
        }
    }

    /// <summary>Checks one call; anything but <see cref="Outcome.Ok"/> is answered with the same 401 and no detail.</summary>
    public Outcome Check(string? nest, string? time, string? nonce, string? signature, string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        // 1. Relay mode is on, and the call is meant for this nest.
        if (_key is null || _nestId is null || !string.Equals(nest, _nestId, StringComparison.Ordinal))
            return Outcome.WrongNest;

        // 2. The signature is 43 characters of strict base64url and verifies. The time and nonce it covers must have
        //    their own shapes (decimal seconds; 22 characters of base64url) before anything is computed.
        if (signature is not { Length: 43 } || !StrictBase64Url.TryDecode(signature, out var given) || given.Length != 32
            || nonce is not { Length: 22 } || !StrictBase64Url.IsBytes(nonce, 16)
            || !TryParseTime(time, out var seconds))
            return Outcome.BadSignature;
        var expected = HMACSHA256.HashData(_key, Message(_nestId, time!, nonce, method, pathAndQuery, body));
        if (!CryptographicOperations.FixedTimeEquals(expected, given))
            return Outcome.BadSignature;

        // 3. Fresh: within two minutes of the nest's clock, either way.
        var now = _clock.GetUtcNow();
        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > (long)MaxSkew.TotalSeconds)
            return Outcome.Stale;

        // 4. Never seen before (a captured call cannot be sent again).
        return _nonces.TryRemember(nonce, now) ? Outcome.Ok : Outcome.Replayed;
    }

    /// <summary>
    /// The signed text: <c>ra1\n{nestId}\n{ts}\n{nonce}\n{METHOD} {pathAndQuery}\n{hex SHA-256 of the body}</c>, as UTF-8.
    /// <paramref name="pathAndQuery"/> is the path as the nest receives it (no <c>/n/&lt;id&gt;</c> prefix).
    /// </summary>
    public static byte[] Message(string nestId, string time, string nonce, string method, string pathAndQuery, ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            Purpose, nestId, time, nonce, method.ToUpperInvariant() + " " + pathAndQuery,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()));

    /// <summary>The <c>X-Pairnets-Sig</c> value for a message (what the service sends; the tests sign with it).</summary>
    public static string Sign(ReadOnlySpan<byte> key, byte[] message) => StrictBase64Url.Encode(HMACSHA256.HashData(key, message));

    /// <summary>Unix seconds in plain decimal: no sign, no leading zeros, no spaces.</summary>
    private static bool TryParseTime(string? text, out long seconds)
    {
        seconds = 0;
        return text is { Length: > 0 and <= 12 } && (text == "0" || text[0] != '0')
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
    }
}

/// <summary>
/// The nonces of accepted calls, kept for <c>lifetime</c> and at most <c>capacity</c> of them, the oldest dropped first.
/// The time window (120 s either way) is far shorter than the lifetime, so a nonce is still known for as long as a
/// call carrying it could pass the time check.
/// </summary>
internal sealed class NonceMemory(TimeSpan lifetime, int capacity)
{
    private readonly Queue<(string Nonce, DateTimeOffset Seen)> _order = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_known)
                return _known.Count;
        }
    }

    /// <summary>False when the nonce was already seen within the lifetime; otherwise it is remembered from now on.</summary>
    public bool TryRemember(string nonce, DateTimeOffset now)
    {
        lock (_known)
        {
            while (_order.Count > 0 && now - _order.Peek().Seen >= lifetime)
                _known.Remove(_order.Dequeue().Nonce);
            if (_known.Contains(nonce))
                return false;
            while (_order.Count >= capacity)
                _known.Remove(_order.Dequeue().Nonce);
            _order.Enqueue((nonce, now));
            _known.Add(nonce);
            return true;
        }
    }
}
