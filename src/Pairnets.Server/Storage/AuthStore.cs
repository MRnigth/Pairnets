using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Pairnets.Core;

namespace Pairnets.Server.Storage;

/// <summary>
/// A computer with its own key. <see cref="Revoked"/> is set once it was removed. <see cref="Access"/> is what it may do
/// (null: the owner, like every computer from before friends); a friend's computer also has <see cref="Person"/>, the
/// line the account page shows ("Anna Berg · a•••@gmail.com"), and <see cref="PersonId"/>, that person's account id at
/// the Pairnets service, which changing or ending a share goes by.
/// </summary>
public sealed record PairedDevice(string Id, string Name, string? System, DateTimeOffset Created, string ApprovedBy, DateTimeOffset? Revoked, string? Icon = null,
    DeviceAccess? Access = null, string? Person = null, string? PersonId = null, string Kind = PairedDevice.ComputerKind, DateTimeOffset? LastUsed = null)
{
    /// <summary>A computer that syncs with its own key.</summary>
    public const string ComputerKind = "computer";

    /// <summary>A WebDAV link's password (Finder, Explorer): it opens <c>/dav</c> and nothing else.</summary>
    public const string WebDavKind = "webdav";

    public bool IsWebDav => Kind == WebDavKind;

    public bool IsActive => Revoked is null;

    /// <summary>What this computer may do: <see cref="Access"/>, or the owner's access when none is written down.</summary>
    public DeviceAccess AccessOrOwner => Access ?? DeviceAccess.Full;

    /// <summary>
    /// How the owner was signed in on the nest's website when they let this computer in, from
    /// <see cref="ApprovedBy"/> ("approved by the owner (google)"): "google", "email", "passkey", "password", or
    /// another way as written ("setup link"). Null when it was not written down (computers let in by older nests).
    /// </summary>
    public string? ApprovalMethod
    {
        get
        {
            var open = ApprovedBy.LastIndexOf('(');
            if (open < 0 || !ApprovedBy.EndsWith(')'))
                return null;
            var method = ApprovedBy[(open + 1)..^1].Trim().ToLowerInvariant();
            return method switch
            {
                "" or "website" => null, // "website": the approval page did not know the sign-in
                "email link" => "email",
                _ => method,
            };
        }
    }
}

/// <summary>
/// auth.db in the data folder: the computers' own keys (only their SHA-256 is stored) and a few settings.
/// Separate from manifest.db, so the file database and its downgrade rules are untouched. Safe to use from
/// the service and the command line at the same time (WAL, a new connection per call).
/// </summary>
public sealed partial class AuthStore
{
    public const int SchemaVersion = 7;

    /// <summary>Prefix of every computer key, so they are easy to recognise (and to keep out of commits).</summary>
    public const string KeyPrefix = "pn_";

    /// <summary>The start of a WebDAV link's stored key ("pd_" and the password without dashes, lower case); never a computer's.</summary>
    public const string WebDavKeyPrefix = "pd_";

    public const string SettingAllowSharedToken = "allowSharedToken";

    public const string SettingNestName = "nestName";

    public const string SettingBigDeletes = "bigDeletes";

    public const int MaxNameLength = 64;

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
        if (version < 5)
        {
            Exec(conn, """
                ALTER TABLE devices ADD COLUMN icon TEXT NULL;
                PRAGMA user_version=5;
                """);
        }
        if (version < 6)
        {
            // Friends (1.0.61.2): every computer already here is the owner's.
            Exec(conn, """
                ALTER TABLE devices ADD COLUMN access TEXT NOT NULL DEFAULT 'owner';
                ALTER TABLE devices ADD COLUMN folders TEXT NULL;
                ALTER TABLE devices ADD COLUMN person TEXT NULL;
                ALTER TABLE devices ADD COLUMN personId TEXT NULL;
                PRAGMA user_version=6;
                """);
        }
        if (version < 7)
        {
            // WebDAV links (feature/webdav): a password that opens /dav only is a device row of its own kind, so removing it,
            // the key cache and the failure throttle work as they do for computers. Every row from before is a computer.
            // Names are unique per kind (UniqueName), so the one name index becomes one per kind. In one transaction: a crash
            // halfway would otherwise leave a column added and the version not, and every later start would fail on it.
            using var tx = conn.BeginTransaction();
            using (var cmd = Command(conn, """
                ALTER TABLE devices ADD COLUMN kind TEXT NOT NULL DEFAULT 'computer';
                ALTER TABLE devices ADD COLUMN lastUsedMs INTEGER NULL;
                DROP INDEX IF EXISTS ix_devices_active_name;
                CREATE UNIQUE INDEX ix_devices_active_name ON devices(kind, name COLLATE NOCASE) WHERE revokedMs IS NULL;
                PRAGMA user_version=7;
                """))
            {
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
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
    /// active computer gets " (2)", " (3)", … so every computer stays recognisable. Without <paramref name="access"/>
    /// it is the owner's; a friend's computer comes with what it may do and whose it is.
    /// </summary>
    public (PairedDevice Device, string Key) AddDevice(string name, string? system, string approvedBy, DeviceAccess? access = null,
        string? person = null, string? personId = null)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var added = AddDevice(conn, tx, name, system, approvedBy, access, person, personId);
        tx.Commit();
        return added;
    }

    private (PairedDevice Device, string Key) AddDevice(SqliteConnection conn, SqliteTransaction tx, string name, string? system, string approvedBy,
        DeviceAccess? access = null, string? person = null, string? personId = null)
    {
        var baseName = CleanName(name) ?? "Computer";
        var key = NewKey();
        var id = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        var unique = UniqueName(conn, tx, baseName, exceptId: null);
        var rights = access ?? DeviceAccess.Full;
        person = Trim(person, MaxPersonLength);
        using (var cmd = Command(conn, """
            INSERT INTO devices(id, name, tokenHash, system, createdMs, approvedBy, revokedMs, access, folders, person, personId)
            VALUES($id, $name, $hash, $system, $created, $by, NULL, $access, $folders, $person, $personId)
            """, ("$id", id), ("$name", unique), ("$hash", HashKey(key)), ("$system", Trim(system, 120)),
            ("$created", now.ToUnixTimeMilliseconds()), ("$by", Trim(approvedBy, 120) ?? "unknown"),
            ("$access", rights.Level), ("$folders", DeviceAccess.FoldersToJson(rights.Folders)), ("$person", person), ("$personId", personId)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        return (new PairedDevice(id, unique, Trim(system, 120), FromMs(now.ToUnixTimeMilliseconds()), approvedBy, null, null, rights, person, personId), key);
    }

    /// <summary>The longest <see cref="PairedDevice.Person"/> line kept.</summary>
    public const int MaxPersonLength = 160;

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
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) && !key.StartsWith(WebDavKeyPrefix, StringComparison.Ordinal))
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
        using var cmd = Command(conn, includeRemoved ? $"{SelectDevice} WHERE kind='computer' ORDER BY createdMs" : $"{SelectDevice} WHERE kind='computer' AND revokedMs IS NULL ORDER BY createdMs");
        using var r = cmd.ExecuteReader();
        var list = new List<PairedDevice>();
        while (r.Read())
            list.Add(ReadDevice(r));
        return list;
    }

    // ------------------------------------------------------------------ WebDAV links

    /// <summary>The most WebDAV links a nest keeps at once.</summary>
    public const int MaxWebDavLinks = 10;

    private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // no i, l, o, 0, 1: easy to read out and type

    /// <summary>A new WebDAV password: 20 of 31 characters (about 99 bits), in groups of five: "abcde-fghjk-mnpqr-stuvw".</summary>
    public static string NewWebDavPassword()
    {
        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = i % 6 == 5 ? '-' : PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
        return new string(chars, 0, 23);
    }

    /// <summary>
    /// The key a typed WebDAV password is stored and found under: lower case, without dashes and spaces (a person types
    /// it into a dialog), behind <see cref="WebDavKeyPrefix"/>. Null for something that cannot be one.
    /// </summary>
    public static string? WebDavKeyOf(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 64)
            return null;
        var clean = password.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        return clean.Length == 20 ? WebDavKeyPrefix + clean : null;
    }

    /// <summary>Makes a WebDAV link: a device of its own kind with what it may do. The password is returned once, never stored.</summary>
    public (PairedDevice Link, string Password) AddWebDavLink(string label, DeviceAccess access)
    {
        var password = NewWebDavPassword();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var baseName = CleanName(label) ?? "WebDAV link";
        var unique = UniqueName(conn, tx, baseName, exceptId: null, PairedDevice.WebDavKind);
        var id = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        using (var cmd = Command(conn, """
            INSERT INTO devices(id, name, tokenHash, system, createdMs, approvedBy, revokedMs, access, folders, person, personId, kind, lastUsedMs)
            VALUES($id, $name, $hash, NULL, $created, 'owner (WebDAV link)', NULL, $access, $folders, NULL, NULL, 'webdav', NULL)
            """, ("$id", id), ("$name", unique), ("$hash", HashKey(WebDavKeyOf(password)!)), ("$created", now.ToUnixTimeMilliseconds()),
            ("$access", access.Level), ("$folders", DeviceAccess.FoldersToJson(access.Folders))))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return (new PairedDevice(id, unique, null, FromMs(now.ToUnixTimeMilliseconds()), "owner (WebDAV link)", null, null, access, null, null,
            PairedDevice.WebDavKind), password);
    }

    /// <summary>The active WebDAV links, oldest first.</summary>
    public IReadOnlyList<PairedDevice> ListWebDavLinks()
    {
        using var conn = Open();
        using var cmd = Command(conn, $"{SelectDevice} WHERE kind='webdav' AND revokedMs IS NULL ORDER BY createdMs");
        using var r = cmd.ExecuteReader();
        var list = new List<PairedDevice>();
        while (r.Read())
            list.Add(ReadDevice(r));
        return list;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _linkTouched = new(StringComparer.Ordinal);

    /// <summary>Remembers that a WebDAV link was used (at most one write a minute per link).</summary>
    public void TouchWebDavLink(string id)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (_linkTouched.TryGetValue(id, out var last) && now - last < 60_000)
            return;
        _linkTouched[id] = now;
        using var conn = Open();
        using var cmd = Command(conn, "UPDATE devices SET lastUsedMs=$now WHERE id=$id AND kind='webdav'", ("$now", now), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The active computers of a friend (never the owner's, whatever they were sent with), oldest first.</summary>
    public IReadOnlyList<PairedDevice> ListFriendDevices(string personId) =>
        ListDevices().Where(d => !d.AccessOrOwner.IsOwner && d.PersonId == personId).ToList();

    /// <summary>
    /// Changes what every active computer of a friend may do (the owner's own computers are never touched). Returns the
    /// computers that changed, as they are now.
    /// </summary>
    public IReadOnlyList<PairedDevice> SetFriendAccess(string personId, DeviceAccess access)
    {
        if (access.IsOwner)
            throw new ArgumentException("A friend's access is edit or view.", nameof(access));
        using (var conn = Open())
        using (var cmd = Command(conn, "UPDATE devices SET access=$access, folders=$folders WHERE personId=$person AND access<>'owner' AND revokedMs IS NULL",
            ("$access", access.Level), ("$folders", DeviceAccess.FoldersToJson(access.Folders)), ("$person", personId)))
            cmd.ExecuteNonQuery();
        return ListFriendDevices(personId);
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

    /// <summary>The icons a computer can have (null = not chosen yet).</summary>
    public static readonly IReadOnlyList<string> Icons = DeviceIcons.All;

    /// <summary>Sets the icon of an active computer. False when it is gone or <paramref name="icon"/> is not one of <see cref="Icons"/>.</summary>
    public bool SetDeviceIcon(string id, string icon)
    {
        if (!DeviceIcons.IsValid(icon))
            return false;
        using var conn = Open();
        using var cmd = Command(conn, "UPDATE devices SET icon=$icon WHERE id=$id AND revokedMs IS NULL", ("$icon", icon), ("$id", id));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// A name the apps can show and send in a header: trimmed, no control, format or bidi characters (see PlainText), at most 64 characters.
    /// Null when nothing usable is left. "unknown" and "another computer" are reserved.
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (name is null)
            return null;
        var clean = PlainText.Clean(name, MaxNameLength);
        if (clean.Length == 0 || clean.Equals("unknown", StringComparison.OrdinalIgnoreCase) || clean.Equals("another computer", StringComparison.OrdinalIgnoreCase))
            return null;
        return clean;
    }

    /// <summary>
    /// <paramref name="name"/>, or "name (2)" and so on, so no two active rows of the same <paramref name="kind"/> share it. Computers
    /// and WebDAV links are named apart: a link called "DESK" does not turn a new computer DESK into "DESK (2)".
    /// </summary>
    private static string UniqueName(SqliteConnection conn, SqliteTransaction tx, string name, string? exceptId, string kind = PairedDevice.ComputerKind)
    {
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{(name.Length > MaxNameLength - 6 ? name[..(MaxNameLength - 6)] : name)} ({n})";
            using var cmd = Command(conn, "SELECT COUNT(*) FROM devices WHERE name=$name COLLATE NOCASE AND revokedMs IS NULL AND id<>$id AND kind=$kind",
                ("$name", candidate), ("$id", exceptId ?? string.Empty), ("$kind", kind));
            cmd.Transaction = tx;
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                return candidate;
        }
    }

    private const string SelectDevice = "SELECT id, name, system, createdMs, approvedBy, revokedMs, icon, access, folders, person, personId, kind, lastUsedMs FROM devices";

    private static PairedDevice ReadDevice(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), FromMs(r.GetInt64(3)), r.GetString(4),
        r.IsDBNull(5) ? null : FromMs(r.GetInt64(5)), r.IsDBNull(6) ? null : r.GetString(6),
        ReadAccess(r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.GetString(11), r.IsDBNull(12) ? null : FromMs(r.GetInt64(12)));

    /// <summary>
    /// What a row may do. A level this server does not know counts as "view" with nothing shared: a damaged or newer row
    /// must never become the owner.
    /// </summary>
    private static DeviceAccess ReadAccess(string level, string? folders) => level switch
    {
        DeviceAccess.Owner => DeviceAccess.Full,
        DeviceAccess.Edit or DeviceAccess.View => new DeviceAccess(level, DeviceAccess.FoldersFromJson(folders)),
        _ => new DeviceAccess(DeviceAccess.View, []),
    };

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

    /// <summary>
    /// The nest's name as its owner gave it on their account page ("soro"); null until the service sends one. The apps
    /// can read it in /api/info.
    /// </summary>
    public string? NestName
    {
        get => GetSetting(SettingNestName) is { Length: > 0 } name ? name : null;
        set => SetSetting(SettingNestName, PlainText.Clean(value, MaxNameLength));
    }

    /// <summary>"Before big deletes" for every computer of the nest (<see cref="BigDeleteModes"/>); asks everywhere until changed.</summary>
    public string BigDeletes
    {
        get => BigDeleteModes.Or(GetSetting(SettingBigDeletes));
        set => SetSetting(SettingBigDeletes, BigDeleteModes.Or(value));
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
