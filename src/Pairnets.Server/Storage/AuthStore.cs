using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Pairnets.Server.Storage;

/// <summary>A computer with its own key. <see cref="Revoked"/> is set once it was removed.</summary>
public sealed record PairedDevice(string Id, string Name, string? System, DateTimeOffset Created, string ApprovedBy, DateTimeOffset? Revoked)
{
    public bool IsActive => Revoked is null;
}

/// <summary>
/// auth.db in the data folder: the computers' own keys (only their SHA-256 is stored) and a few settings.
/// Separate from manifest.db, so the file database and its downgrade rules are untouched. Safe to use from
/// the service and the command line at the same time (WAL, a new connection per call).
/// </summary>
public sealed partial class AuthStore
{
    public const int SchemaVersion = 4;

    /// <summary>Prefix of every computer key, so they are easy to recognise (and to keep out of commits).</summary>
    public const string KeyPrefix = "pn_";

    public const string SettingAllowSharedToken = "allowSharedToken";

    private const int MaxNameLength = 64;

    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    public AuthStore(ServerPaths paths, TimeProvider? clock = null)
        : this(paths.AuthDatabase, clock)
    {
    }

    public AuthStore(string databasePath, TimeProvider? clock = null)
    {
        DatabasePath = databasePath;
        _clock = clock ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Migrate(conn);
    }

    public string DatabasePath { get; }

    private void Migrate(SqliteConnection conn)
    {
        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException($"auth.db schema {version} is newer than this server supports ({SchemaVersion}).");
        if (version < 1)
        {
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS devices(
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    tokenHash BLOB NOT NULL UNIQUE,
                    system TEXT NULL,
                    createdMs INTEGER NOT NULL,
                    approvedBy TEXT NOT NULL,
                    revokedMs INTEGER NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_devices_active_name ON devices(name COLLATE NOCASE) WHERE revokedMs IS NULL;
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
                PRAGMA user_version=1;
                """);
        }
        if (version < 2)
        {
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS sessions(
                    id TEXT PRIMARY KEY NOT NULL,
                    secretHash BLOB NOT NULL UNIQUE,
                    method TEXT NOT NULL,
                    userAgent TEXT NULL,
                    createdMs INTEGER NOT NULL,
                    lastSeenMs INTEGER NOT NULL,
                    expiresMs INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS setupLinks(
                    codeHash BLOB PRIMARY KEY NOT NULL,
                    createdMs INTEGER NOT NULL,
                    expiresMs INTEGER NOT NULL,
                    usedMs INTEGER NULL);
                CREATE TABLE IF NOT EXISTS pairRequests(
                    id TEXT PRIMARY KEY NOT NULL,
                    userCode TEXT NOT NULL UNIQUE,
                    secretHash BLOB NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    system TEXT NULL,
                    appVersion TEXT NULL,
                    address TEXT NULL,
                    createdMs INTEGER NOT NULL,
                    expiresMs INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    decidedMs INTEGER NULL,
                    decidedBy TEXT NULL,
                    deviceId TEXT NULL);
                PRAGMA user_version=2;
                """);
        }
        if (version < 3)
        {
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS passkeys(
                    id TEXT PRIMARY KEY NOT NULL,
                    credentialId BLOB NOT NULL UNIQUE,
                    publicKey BLOB NOT NULL,
                    algorithm INTEGER NOT NULL,
                    signCount INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    createdMs INTEGER NOT NULL,
                    lastUsedMs INTEGER NULL);
                PRAGMA user_version=3;
                """);
        }
        if (version < 4)
        {
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS emailLinks(
                    codeHash BLOB PRIMARY KEY NOT NULL,
                    purpose TEXT NOT NULL,
                    email TEXT NOT NULL,
                    createdMs INTEGER NOT NULL,
                    expiresMs INTEGER NOT NULL,
                    usedMs INTEGER NULL);
                PRAGMA user_version=4;
                """);
        }
    }

    // ------------------------------------------------------------------ keys

    /// <summary>A new random key: "pn_" and 32 random bytes, base64url.</summary>
    public static string NewKey() => KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ------------------------------------------------------------------ devices

    /// <summary>
    /// Adds a computer and returns it with its key (shown once, never stored). A name already used by an
    /// active computer gets " (2)", " (3)", … so every computer stays recognisable.
    /// </summary>
    public (PairedDevice Device, string Key) AddDevice(string name, string? system, string approvedBy)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var added = AddDevice(conn, tx, name, system, approvedBy);
        tx.Commit();
        return added;
    }

    private (PairedDevice Device, string Key) AddDevice(SqliteConnection conn, SqliteTransaction tx, string name, string? system, string approvedBy)
    {
        var baseName = CleanName(name) ?? "Computer";
        var key = NewKey();
        var id = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        var unique = UniqueName(conn, tx, baseName, exceptId: null);
        using (var cmd = Command(conn, """
            INSERT INTO devices(id, name, tokenHash, system, createdMs, approvedBy, revokedMs)
            VALUES($id, $name, $hash, $system, $created, $by, NULL)
            """, ("$id", id), ("$name", unique), ("$hash", HashKey(key)), ("$system", Trim(system, 120)),
            ("$created", now.ToUnixTimeMilliseconds()), ("$by", Trim(approvedBy, 120) ?? "unknown")))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        return (new PairedDevice(id, unique, Trim(system, 120), FromMs(now.ToUnixTimeMilliseconds()), approvedBy, null), key);
    }

    /// <summary>Gives an active computer a new key (the old one stops working). Null when it is unknown or removed.</summary>
    public string? ReplaceKey(string id)
    {
        var key = NewKey();
        using var conn = Open();
        using var cmd = Command(conn, "UPDATE devices SET tokenHash=$hash WHERE id=$id AND revokedMs IS NULL", ("$hash", HashKey(key)), ("$id", id));
        return cmd.ExecuteNonQuery() == 1 ? key : null;
    }

    /// <summary>The computer this key belongs to (also when it was removed), or null for an unknown key.</summary>
    public PairedDevice? FindByKey(string key)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return null;
        using var conn = Open();
        using var cmd = Command(conn, $"{SelectDevice} WHERE tokenHash=$hash", ("$hash", HashKey(key)));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDevice(r) : null;
    }

    public PairedDevice? GetDevice(string id)
    {
        using var conn = Open();
        using var cmd = Command(conn, $"{SelectDevice} WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDevice(r) : null;
    }

    /// <summary>The active computer with this name (case-insensitive), or null.</summary>
    public PairedDevice? FindActiveByName(string name)
    {
        using var conn = Open();
        using var cmd = Command(conn, $"{SelectDevice} WHERE name=$name COLLATE NOCASE AND revokedMs IS NULL", ("$name", name));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDevice(r) : null;
    }

    public IReadOnlyList<PairedDevice> ListDevices(bool includeRemoved = false)
    {
        using var conn = Open();
        using var cmd = Command(conn, includeRemoved ? $"{SelectDevice} ORDER BY createdMs" : $"{SelectDevice} WHERE revokedMs IS NULL ORDER BY createdMs");
        using var r = cmd.ExecuteReader();
        var list = new List<PairedDevice>();
        while (r.Read())
            list.Add(ReadDevice(r));
        return list;
    }

    /// <summary>Removes a computer: its key stops working. Returns false when it was unknown or already removed.</summary>
    public bool RemoveDevice(string id)
    {
        using var conn = Open();
        using var cmd = Command(conn, "UPDATE devices SET revokedMs=$now WHERE id=$id AND revokedMs IS NULL",
            ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Renames an active computer (made unique like <see cref="AddDevice"/>). Returns the result, or null.</summary>
    public PairedDevice? RenameDevice(string id, string name)
    {
        var clean = CleanName(name);
        if (clean is null)
            return null;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var unique = UniqueName(conn, tx, clean, exceptId: id);
        using (var cmd = Command(conn, "UPDATE devices SET name=$name WHERE id=$id AND revokedMs IS NULL", ("$name", unique), ("$id", id)))
        {
            cmd.Transaction = tx;
            if (cmd.ExecuteNonQuery() != 1)
                return null;
        }
        tx.Commit();
        return GetDevice(id);
    }

    /// <summary>
    /// A name the apps can show and send in a header: trimmed, no control characters, at most 64 characters.
    /// Null when nothing usable is left. "unknown" and "another computer" are reserved.
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (name is null)
            return null;
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxNameLength)
            clean = clean[..MaxNameLength].TrimEnd();
        if (clean.Length == 0 || clean.Equals("unknown", StringComparison.OrdinalIgnoreCase) || clean.Equals("another computer", StringComparison.OrdinalIgnoreCase))
            return null;
        return clean;
    }

    private static string UniqueName(SqliteConnection conn, SqliteTransaction tx, string name, string? exceptId)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{(name.Length > MaxNameLength - 6 ? name[..(MaxNameLength - 6)] : name)} ({n})";
            using var cmd = Command(conn, "SELECT COUNT(*) FROM devices WHERE name=$name COLLATE NOCASE AND revokedMs IS NULL AND id<>$id",
                ("$name", candidate), ("$id", exceptId ?? string.Empty));
            cmd.Transaction = tx;
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                return candidate;
        }
    }

    private const string SelectDevice = "SELECT id, name, system, createdMs, approvedBy, revokedMs FROM devices";

    private static PairedDevice ReadDevice(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), FromMs(r.GetInt64(3)), r.GetString(4),
        r.IsDBNull(5) ? null : FromMs(r.GetInt64(5)));

    // ------------------------------------------------------------------ settings

    /// <summary>
    /// Whether the shared SYNC_TOKEN still works for syncing. On by default so computers set up before
    /// per-computer keys keep working; turned off on the nest's Security page once they all switched.
    /// </summary>
    public bool AllowSharedToken
    {
        get => GetSetting(SettingAllowSharedToken) is not "false";
        set => SetSetting(SettingAllowSharedToken, value ? "true" : "false");
    }

    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT value FROM settings WHERE key=$k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd = Command(conn, "INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ plumbing

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Exec(conn, "PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;");
        return conn;
    }

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;

    private static SqliteCommand Command(SqliteConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = Command(conn, sql);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = Command(conn, sql);
        return cmd.ExecuteScalar();
    }
}
