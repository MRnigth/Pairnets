using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Pairnets.Server.Storage;

/// <summary>A passkey the owner signs in with (the nest keeps only its public key).</summary>
public sealed record StoredPasskey(string Id, byte[] CredentialId, byte[] PublicKey, int Algorithm, uint SignCount, string Name,
    DateTimeOffset Created, DateTimeOffset? LastUsed);

public sealed partial class AuthStore
{
    private const string SelectPasskey = "SELECT id, credentialId, publicKey, algorithm, signCount, name, createdMs, lastUsedMs FROM passkeys";

    /// <summary>The random handle that identifies the owner to authenticators; created on first use.</summary>
    public byte[] UserHandle()
    {
        if (GetSetting(SettingUserHandle) is { } stored && Convert.FromBase64String(stored) is { Length: 32 } handle)
            return handle;
        var created = RandomNumberGenerator.GetBytes(32);
        SetSetting(SettingUserHandle, Convert.ToBase64String(created));
        return created;
    }

    public int CountPasskeys()
    {
        using var conn = Open();
        return Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM passkeys"), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<StoredPasskey> ListPasskeys()
    {
        using var conn = Open();
        using var cmd = Command(conn, $"{SelectPasskey} ORDER BY createdMs");
        using var r = cmd.ExecuteReader();
        var list = new List<StoredPasskey>();
        while (r.Read())
            list.Add(ReadPasskey(r));
        return list;
    }

    public StoredPasskey? FindPasskey(byte[] credentialId)
    {
        using var conn = Open();
        using var cmd = Command(conn, $"{SelectPasskey} WHERE credentialId=$id", ("$id", credentialId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPasskey(r) : null;
    }

    /// <summary>Stores a new passkey. False when this authenticator credential is already registered.</summary>
    public bool AddPasskey(byte[] credentialId, byte[] publicKey, int algorithm, uint signCount, string? name)
    {
        try
        {
            using var conn = Open();
            using var cmd = Command(conn, """
                INSERT INTO passkeys(id, credentialId, publicKey, algorithm, signCount, name, createdMs, lastUsedMs)
                VALUES($id, $cred, $key, $alg, $count, $name, $now, NULL)
                """, ("$id", Base64Url(RandomNumberGenerator.GetBytes(9))), ("$cred", credentialId), ("$key", publicKey), ("$alg", algorithm),
                ("$count", (long)signCount), ("$name", CleanLabel(name) ?? "Passkey"), ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()));
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    /// <summary>Records a successful sign-in with a passkey: its new counter and the time.</summary>
    public void TouchPasskey(string id, uint signCount)
    {
        using var conn = Open();
        using var cmd = Command(conn, "UPDATE passkeys SET signCount=$count, lastUsedMs=$now WHERE id=$id",
            ("$count", (long)signCount), ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public bool RemovePasskey(string id)
    {
        using var conn = Open();
        using var cmd = Command(conn, "DELETE FROM passkeys WHERE id=$id", ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>A label for a passkey or browser: no control characters, at most 64 characters, or null.</summary>
    public static string? CleanLabel(string? text)
    {
        if (text is null)
            return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 64)
            clean = clean[..64].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    private static StoredPasskey ReadPasskey(SqliteDataReader r) => new(
        r.GetString(0), (byte[])r["credentialId"], (byte[])r["publicKey"], r.GetInt32(3), (uint)r.GetInt64(4), r.GetString(5),
        FromMs(r.GetInt64(6)), r.IsDBNull(7) ? null : FromMs(r.GetInt64(7)));
}
