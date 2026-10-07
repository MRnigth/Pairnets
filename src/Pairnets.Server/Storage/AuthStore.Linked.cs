using System.Security.Cryptography;

namespace Pairnets.Server.Storage;

/// <summary>What an emailed link does when it is used.</summary>
public static class EmailLinkPurpose
{
    /// <summary>Signs the owner in (the address is already confirmed).</summary>
    public const string SignIn = "signin";

    /// <summary>Confirms a new address for sign-in links, and signs in.</summary>
    public const string Confirm = "confirm";
}

public sealed record EmailLink(string Purpose, string Email);

/// <summary>Email addresses and a Google account the owner signs in with.</summary>
public sealed partial class AuthStore
{
    public const string SettingEmail = "owner.email";
    public const string SettingGoogleSub = "owner.googleSub";
    public const string SettingGoogleEmail = "owner.googleEmail";

    /// <summary>How long an emailed link works (once).</summary>
    public static readonly TimeSpan EmailLinkLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The confirmed address that sign-in links are sent to, or null.</summary>
    public string? OwnerEmail => GetSetting(SettingEmail);

    public void SetOwnerEmail(string? email)
    {
        if (email is null)
            DeleteSetting(SettingEmail);
        else
            SetSetting(SettingEmail, email);
    }

    /// <summary>The Google account (its stable "sub" id and email) linked to the nest, or null.</summary>
    public (string Sub, string Email)? GoogleAccount =>
        GetSetting(SettingGoogleSub) is { Length: > 0 } sub ? (sub, GetSetting(SettingGoogleEmail) ?? string.Empty) : null;

    public void SetGoogleAccount(string? sub, string? email)
    {
        if (sub is null)
        {
            DeleteSetting(SettingGoogleSub);
            DeleteSetting(SettingGoogleEmail);
            return;
        }
        SetSetting(SettingGoogleSub, sub);
        SetSetting(SettingGoogleEmail, email ?? string.Empty);
    }

    /// <summary>A one-time code for an emailed link (15 minutes). Only its hash is kept.</summary>
    public string CreateEmailLink(string purpose, string email)
    {
        var code = Base64Url(RandomNumberGenerator.GetBytes(24));
        var now = _clock.GetUtcNow();
        using var conn = Open();
        Purge(conn);
        using var cmd = Command(conn, "INSERT INTO emailLinks(codeHash, purpose, email, createdMs, expiresMs, usedMs) VALUES($hash, $purpose, $email, $now, $expires, NULL)",
            ("$hash", HashKey(code)), ("$purpose", purpose), ("$email", email), ("$now", now.ToUnixTimeMilliseconds()),
            ("$expires", (now + EmailLinkLifetime).ToUnixTimeMilliseconds()));
        cmd.ExecuteNonQuery();
        return code;
    }

    /// <summary>Uses up an emailed link: what it was for, once, and only before it expires. Null otherwise.</summary>
    public EmailLink? UseEmailLink(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 100)
            return null;
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        EmailLink? link = null;
        using (var read = Command(conn, "SELECT purpose, email FROM emailLinks WHERE codeHash=$hash AND usedMs IS NULL AND expiresMs>$now", ("$hash", HashKey(code)), ("$now", now)))
        {
            read.Transaction = tx;
            using var r = read.ExecuteReader();
            if (r.Read())
                link = new EmailLink(r.GetString(0), r.GetString(1));
        }
        if (link is null)
            return null;
        using (var use = Command(conn, "UPDATE emailLinks SET usedMs=$now WHERE codeHash=$hash AND usedMs IS NULL", ("$now", now), ("$hash", HashKey(code))))
        {
            use.Transaction = tx;
            if (use.ExecuteNonQuery() != 1)
                return null;
        }
        tx.Commit();
        return link;
    }
}
