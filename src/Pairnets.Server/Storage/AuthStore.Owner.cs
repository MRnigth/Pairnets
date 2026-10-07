using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Pairnets.Server.Storage;

/// <summary>A browser signed in to the nest's website. Only the SHA-256 of its cookie is stored.</summary>
public sealed record OwnerSession(string Id, string Method, string? UserAgent, DateTimeOffset Created, DateTimeOffset LastSeen, DateTimeOffset Expires);

/// <summary>The nest's owner: one person (it is their server). How they sign in to its website.</summary>
public sealed partial class AuthStore
{
    public const string SettingPasswordHash = "owner.passwordHash";
    public const string SettingPasswordSetMs = "owner.passwordSetMs";

    /// <summary>The user handle every passkey of this nest is made with (random, never shown, the same for the owner's life).</summary>
    public const string SettingUserHandle = "owner.userHandle";

    /// <summary>The nest's own address as the running server knows it (for the command line, which lacks its environment).</summary>
    public const string SettingPublicUrl = "server.publicUrl";

    /// <summary>How long a browser stays signed in without visiting.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    /// <summary>How long a setup link printed by "tether-server owner-link" works (once).</summary>
    public static readonly TimeSpan SetupLinkLifetime = TimeSpan.FromHours(24);

    // ------------------------------------------------------------------ password

    /// <summary>The stored password hash (ASP.NET's PBKDF2 format), or null when no password is set.</summary>
    public string? PasswordHash => GetSetting(SettingPasswordHash);

    public DateTimeOffset? PasswordSetAt =>
        long.TryParse(GetSetting(SettingPasswordSetMs), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? FromMs(ms) : null;

    public void SetPasswordHash(string? hash)
    {
        if (hash is null)
        {
            DeleteSetting(SettingPasswordHash);
            DeleteSetting(SettingPasswordSetMs);
            return;
        }
        SetSetting(SettingPasswordHash, hash);
        SetSetting(SettingPasswordSetMs, _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// True once the owner has some way to sign in (a password, a passkey, an email address, a Google account).
    /// Until then only a setup link opens the website.
    /// </summary>
    public bool HasSignInMethod => PasswordHash is not null || CountSignInExtras() > 0;

    /// <summary>Sign-in methods kept outside the password: passkeys, a confirmed email address, a linked Google account.</summary>
    private int CountSignInExtras() => CountPasskeys() + (OwnerEmail is null ? 0 : 1) + (GoogleAccount is null ? 0 : 1);

    // ------------------------------------------------------------------ sessions

    /// <summary>Signs a browser in. Returns the session and the secret for its cookie (never stored).</summary>
    public (OwnerSession Session, string Secret) CreateSession(string method, string? userAgent)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var id = Base64Url(RandomNumberGenerator.GetBytes(9));
        var now = _clock.GetUtcNow();
        var expires = now + SessionLifetime;
        using var conn = Open();
        Purge(conn);
        using var cmd = Command(conn, """
            INSERT INTO sessions(id, secretHash, method, userAgent, createdMs, lastSeenMs, expiresMs)
            VALUES($id, $hash, $method, $ua, $now, $now, $expires)
            """, ("$id", id), ("$hash", HashKey(secret)), ("$method", Trim(method, 60) ?? "unknown"), ("$ua", Trim(userAgent, 300)),
            ("$now", now.ToUnixTimeMilliseconds()), ("$expires", expires.ToUnixTimeMilliseconds()));
        cmd.ExecuteNonQuery();
        return (new OwnerSession(id, method, Trim(userAgent, 300), now, now, expires), secret);
    }

    /// <summary>
    /// The session behind a cookie, or null when it is unknown or expired. A session in use is extended
    /// (at most once an hour, to keep writes rare).
    /// </summary>
    public OwnerSession? FindSession(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 100)
            return null;
        var now = _clock.GetUtcNow();
        using var conn = Open();
        OwnerSession? session;
        using (var cmd = Command(conn, $"{SelectSession} WHERE secretHash=$hash AND expiresMs>$now",
            ("$hash", HashKey(secret)), ("$now", now.ToUnixTimeMilliseconds())))
        using (var r = cmd.ExecuteReader())
            session = r.Read() ? ReadSession(r) : null;
        if (session is not null && now - session.LastSeen > TimeSpan.FromHours(1))
        {
            using var touch = Command(conn, "UPDATE sessions SET lastSeenMs=$now, expiresMs=$expires WHERE id=$id",
                ("$now", now.ToUnixTimeMilliseconds()), ("$expires", (now + SessionLifetime).ToUnixTimeMilliseconds()), ("$id", session.Id));
            touch.ExecuteNonQuery();
            session = session with { LastSeen = now, Expires = now + SessionLifetime };
        }
        return session;
    }

    public IReadOnlyList<OwnerSession> ListSessions()
    {
        using var conn = Open();
        using var cmd = Command(conn, $"{SelectSession} WHERE expiresMs>$now ORDER BY lastSeenMs DESC", ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        using var r = cmd.ExecuteReader();
        var list = new List<OwnerSession>();
        while (r.Read())
            list.Add(ReadSession(r));
        return list;
    }

    public bool DeleteSession(string id)
    {
        using var conn = Open();
        using var cmd = Command(conn, "DELETE FROM sessions WHERE id=$id", ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    private const string SelectSession = "SELECT id, method, userAgent, createdMs, lastSeenMs, expiresMs FROM sessions";

    private static OwnerSession ReadSession(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), FromMs(r.GetInt64(3)), FromMs(r.GetInt64(4)), FromMs(r.GetInt64(5)));

    // ------------------------------------------------------------------ setup links

    /// <summary>A one-time code for the nest's setup page (printed by "tether-server owner-link" and install.sh).</summary>
    public string CreateSetupCode()
    {
        var code = Base64Url(RandomNumberGenerator.GetBytes(24));
        var now = _clock.GetUtcNow();
        using var conn = Open();
        Purge(conn);
        using var cmd = Command(conn, "INSERT INTO setupLinks(codeHash, createdMs, expiresMs, usedMs) VALUES($hash, $now, $expires, NULL)",
            ("$hash", HashKey(code)), ("$now", now.ToUnixTimeMilliseconds()), ("$expires", (now + SetupLinkLifetime).ToUnixTimeMilliseconds()));
        cmd.ExecuteNonQuery();
        return code;
    }

    /// <summary>Uses up a setup code. True only once, and only before it expires.</summary>
    public bool UseSetupCode(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 100)
            return false;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = Open();
        using var cmd = Command(conn, "UPDATE setupLinks SET usedMs=$now WHERE codeHash=$hash AND usedMs IS NULL AND expiresMs>$now",
            ("$now", now), ("$hash", HashKey(code)));
        return cmd.ExecuteNonQuery() == 1;
    }

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Forgets expired sessions, used or expired setup links and old pairing requests.</summary>
    private void Purge(SqliteConnection conn)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var dayAgo = now - (long)TimeSpan.FromDays(1).TotalMilliseconds;
        using var cmd = Command(conn, """
            DELETE FROM sessions WHERE expiresMs<=$now;
            DELETE FROM setupLinks WHERE expiresMs<=$now OR usedMs IS NOT NULL;
            DELETE FROM pairRequests WHERE expiresMs<=$dayAgo;
            """, ("$now", now), ("$dayAgo", dayAgo));
        cmd.ExecuteNonQuery();
    }

    public void DeleteSetting(string key)
    {
        using var conn = Open();
        using var cmd = Command(conn, "DELETE FROM settings WHERE key=$k", ("$k", key));
        cmd.ExecuteNonQuery();
    }
}
