using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Pairnets.Server.Storage;

/// <summary>
/// A computer asking to join the nest. It shows <see cref="UserCode"/> so you can check it is the one
/// you are approving; it learns the answer with a secret only it knows (stored as SHA-256).
/// </summary>
public sealed record PairRequest(
    string Id,
    string UserCode,
    string Name,
    string? System,
    string? AppVersion,
    string? Address,
    DateTimeOffset Created,
    DateTimeOffset Expires,
    string Status,
    DateTimeOffset? Decided,
    string? DecidedBy,
    string? DeviceId)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Delivered = "delivered";
    public const string Expired = "expired";

    /// <summary>"KQ7M-4PXD": the code as people see and type it.</summary>
    public string DisplayCode => UserCode.Length == 8 ? UserCode[..4] + "-" + UserCode[4..] : UserCode;

    /// <summary><see cref="Status"/>, with a pending or approved request past its time reported as expired.</summary>
    public string StatusAt(DateTimeOffset now) => Status is Pending or Approved && now >= Expires ? Expired : Status;
}

public sealed partial class AuthStore
{
    /// <summary>How long a computer has to be approved.</summary>
    public static readonly TimeSpan PairRequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>After approval the computer has this long to collect its key (it asks every 2 seconds).</summary>
    public static readonly TimeSpan PairPickupWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The code alphabet: no vowels (no accidental words), no 0/O or 1/I, so it reads aloud and types easily.
    /// 28^8 ≈ 3.8·10^11 codes, and a request lives 10 minutes.
    /// </summary>
    private const string CodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ23456789";

    /// <summary>Starts a request. Returns it and the secret the computer polls with (shown once, stored hashed).</summary>
    public (PairRequest Request, string Secret) CreatePairRequest(string name, string? system, string? appVersion, string? address)
    {
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var id = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        var expires = now + PairRequestLifetime;
        using var conn = Open();
        Purge(conn);
        for (var attempt = 0; ; attempt++)
        {
            var code = NewUserCode();
            try
            {
                using var cmd = Command(conn, """
                    INSERT INTO pairRequests(id, userCode, secretHash, name, system, appVersion, address, createdMs, expiresMs, status)
                    VALUES($id, $code, $hash, $name, $system, $version, $address, $now, $expires, 'pending')
                    """, ("$id", id), ("$code", code), ("$hash", HashKey(secret)), ("$name", CleanName(name) ?? "Computer"),
                    ("$system", Trim(system, 120)), ("$version", Trim(appVersion, 32)), ("$address", Trim(address, 64)),
                    ("$now", now.ToUnixTimeMilliseconds()), ("$expires", expires.ToUnixTimeMilliseconds()));
                cmd.ExecuteNonQuery();
                return (GetPairRequest(conn, "id=$v", id)!, secret);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && attempt < 5)
            {
                // The code is already taken by an older request (it stays unique for a day): draw another.
            }
        }
    }

    /// <summary>Requests still waiting for an answer (newest first), optionally only those from one address.</summary>
    public IReadOnlyList<PairRequest> ListPendingPairRequests(string? address = null)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = Open();
        using var cmd = Command(conn, address is null
            ? $"{SelectPair} WHERE status='pending' AND expiresMs>$now ORDER BY createdMs DESC"
            : $"{SelectPair} WHERE status='pending' AND expiresMs>$now AND address=$address ORDER BY createdMs DESC",
            ("$now", now), ("$address", address));
        using var r = cmd.ExecuteReader();
        var list = new List<PairRequest>();
        while (r.Read())
            list.Add(ReadPair(r));
        return list;
    }

    /// <summary>The request with this code ("KQ7M-4PXD", "kq7m4pxd", "KQ7M 4PXD" all work), or null.</summary>
    public PairRequest? FindPairRequestByCode(string? code)
    {
        var normal = NormalizeCode(code);
        if (normal is null)
            return null;
        using var conn = Open();
        return GetPairRequest(conn, "userCode=$v", normal);
    }

    /// <summary>The request a computer polls with its secret, or null.</summary>
    public PairRequest? FindPairRequestBySecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length > 100)
            return null;
        using var conn = Open();
        return GetPairRequest(conn, "secretHash=$v", HashKey(secret));
    }

    /// <summary>Approves or denies a request that is still waiting. Returns false when it was not (any more).</summary>
    public bool DecidePairRequest(string id, bool approve, string decidedBy)
    {
        var now = _clock.GetUtcNow();
        using var conn = Open();
        using var cmd = Command(conn, """
            UPDATE pairRequests SET status=$status, decidedMs=$now, decidedBy=$by, expiresMs=CASE WHEN $approve THEN $pickup ELSE expiresMs END
            WHERE id=$id AND status='pending' AND expiresMs>$now
            """, ("$status", approve ? PairRequest.Approved : PairRequest.Denied), ("$now", now.ToUnixTimeMilliseconds()),
            ("$by", Trim(decidedBy, 120)), ("$approve", approve), ("$pickup", (now + PairPickupWindow).ToUnixTimeMilliseconds()), ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// An approved computer collects its key: the computer is added now (not at approval, so an approval
    /// nobody collects leaves nothing behind) and the request can never hand out a second key.
    /// </summary>
    public (PairedDevice Device, string Key)? DeliverPairRequest(string id)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var request = GetPairRequest(conn, "id=$v", id, tx);
        if (request is not { Status: PairRequest.Approved } || request.Expires.ToUnixTimeMilliseconds() <= now)
            return null;
        var added = AddDevice(conn, tx, request.Name, request.System, "approved by " + (request.DecidedBy ?? "the owner"));
        using (var cmd = Command(conn, "UPDATE pairRequests SET status='delivered', deviceId=$device WHERE id=$id AND status='approved'",
            ("$device", added.Device.Id), ("$id", id)))
        {
            cmd.Transaction = tx;
            if (cmd.ExecuteNonQuery() != 1)
                return null;
        }
        tx.Commit();
        return added;
    }

    /// <summary>"KQ7M-4PXD" → "KQ7M4PXD"; null for anything that cannot be a code.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (code is null)
            return null;
        var sb = new StringBuilder(8);
        foreach (var c in code)
        {
            if (c is '-' or ' ')
                continue;
            var upper = char.ToUpperInvariant(c);
            if (!CodeAlphabet.Contains(upper, StringComparison.Ordinal) || sb.Length == 8)
                return null;
            sb.Append(upper);
        }
        return sb.Length == 8 ? sb.ToString() : null;
    }

    private static string NewUserCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    private const string SelectPair = "SELECT id, userCode, name, system, appVersion, address, createdMs, expiresMs, status, decidedMs, decidedBy, deviceId FROM pairRequests";

    private static PairRequest? GetPairRequest(SqliteConnection conn, string where, object value, SqliteTransaction? tx = null)
    {
        using var cmd = Command(conn, $"{SelectPair} WHERE {where}", ("$v", value));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPair(r) : null;
    }

    private static PairRequest ReadPair(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), FromMs(r.GetInt64(6)), FromMs(r.GetInt64(7)), r.GetString(8),
        r.IsDBNull(9) ? null : FromMs(r.GetInt64(9)), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11));
}
