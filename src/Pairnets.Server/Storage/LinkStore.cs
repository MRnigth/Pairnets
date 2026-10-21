using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pairnets.Core;

namespace Pairnets.Server.Storage;

/// <summary>What a linked nest may do on the nest that shares with it: take changes in only, or send its own too.</summary>
public enum LinkAccess
{
    View,
    Edit,
}

/// <summary>How a link is doing, as the apps and the account page show it.</summary>
public enum LinkState
{
    /// <summary>Set up, no pass finished yet.</summary>
    New,

    /// <summary>The last pass reached the other nest and finished.</summary>
    InStep,

    /// <summary>The other nest could not be reached (off, or the service is down); the copy here works as normal.</summary>
    Waiting,

    /// <summary>The other nest refused (key removed, encrypted, too old): nothing goes across until it is fixed.</summary>
    Problem,

    /// <summary>Ended by either owner. The copy stays and stops changing; the key is forgotten.</summary>
    Unlinked,
}

/// <summary>
/// One linked nest (docs/DECISIONS.md, "4 nests per account, and linked nests"): this nest keeps <see cref="Folder"/>, a
/// top-level folder of its own, in step with <see cref="Folders"/> (or everything) on the nest <see cref="RemoteNestId"/>.
/// <see cref="Key"/> is this nest's key on the other nest, null once unlinked.
/// </summary>
public sealed record NestLink(
    string Id,
    string RemoteNestId,
    string DeviceId,
    string? Key,
    string Folder,
    LinkAccess Access,
    IReadOnlyList<string>? Folders,
    string OwnerName,
    string NestName,
    LinkState State,
    string? RemoteServerId,
    long RemoteCursor,
    long LocalCursor,
    DateTimeOffset Created,
    DateTimeOffset? LastInStep,
    string? LastError)
{
    public bool IsActive => State != LinkState.Unlinked && Key is not null;

    /// <summary>A path of the other nest ("Photos/a.jpg") that this link shares: everything, or one of its folders.</summary>
    public bool Shares(string remotePath)
    {
        if (Folders is null)
            return true;
        var slash = remotePath.IndexOf('/');
        var top = slash < 0 ? remotePath : remotePath[..slash];
        return Folders.Any(f => string.Equals(f, top, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"From Oskar – Home nest/Photos/a.jpg" for "Photos/a.jpg".</summary>
    public string LocalPath(string remotePath) => Folder + "/" + remotePath;

    /// <summary>"Photos/a.jpg" for a path in this link's folder, else null.</summary>
    public string? RemotePath(string localPath) =>
        localPath.Length > Folder.Length + 1 && localPath.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase)
            ? localPath[(Folder.Length + 1)..]
            : null;
}

/// <summary>The hashes a file had on both nests the last time a link brought them into step.</summary>
public sealed record LinkFile(string Path, string? RemoteHash, string? LocalHash);

/// <summary>
/// links.db in the data folder: the nests this nest is linked to and what each pass last saw. It is its own file, so
/// auth.db's schema stays the computers' own.
/// </summary>
public sealed class LinkStore
{
    public const int SchemaVersion = 1;

    /// <summary>A nest can be linked to at most this many nests (docs/DECISIONS.md).</summary>
    public const int MaxLinks = 4;

    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    public LinkStore(ServerPaths paths, TimeProvider? clock = null)
        : this(Path.Combine(paths.DataDir, "links.db"), clock)
    {
    }

    public LinkStore(string databasePath, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
            throw new InvalidOperationException($"links.db is from a newer Pairnets (schema {version}). Update the server.");
        if (version < 1)
        {
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS links(
                    id TEXT PRIMARY KEY NOT NULL,
                    remoteNestId TEXT NOT NULL,
                    deviceId TEXT NOT NULL,
                    key TEXT NULL,
                    folder TEXT NOT NULL,
                    access TEXT NOT NULL,
                    folders TEXT NULL,
                    ownerName TEXT NOT NULL,
                    nestName TEXT NOT NULL,
                    state TEXT NOT NULL,
                    remoteServerId TEXT NULL,
                    remoteCursor INTEGER NOT NULL DEFAULT 0,
                    localCursor INTEGER NOT NULL DEFAULT 0,
                    createdMs INTEGER NOT NULL,
                    lastInStepMs INTEGER NULL,
                    lastError TEXT NULL);
                CREATE TABLE IF NOT EXISTS linkFiles(
                    linkId TEXT NOT NULL,
                    path TEXT NOT NULL,
                    remoteHash TEXT NULL,
                    localHash TEXT NULL,
                    PRIMARY KEY(linkId, path));
                PRAGMA user_version=1;
                """);
        }
    }

    public IReadOnlyList<NestLink> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM links ORDER BY createdMs, id;";
        using var r = cmd.ExecuteReader();
        var list = new List<NestLink>();
        while (r.Read())
            list.Add(Read(r));
        return list;
    }

    public NestLink? Get(string id) => List().FirstOrDefault(l => l.Id == id);

    /// <summary>The top-level folders that came in through a link (still linked or not): never passed on to a third nest.</summary>
    public IReadOnlyList<string> LinkedInFolders() => List().Select(l => l.Folder).ToList();

    /// <summary>
    /// Adds a link, or sets it up again with a new key when the service sends the same id once more. Null when the nest
    /// already has <see cref="MaxLinks"/> other links or another link uses the folder.
    /// </summary>
    public NestLink? Add(string id, string remoteNestId, string deviceId, string key, string folder, LinkAccess access,
        IReadOnlyList<string>? folders, string ownerName, string nestName)
    {
        var others = List().Where(l => l.Id != id).ToList();
        if (others.Count(l => l.IsActive) >= MaxLinks || others.Any(l => string.Equals(l.Folder, folder, StringComparison.OrdinalIgnoreCase)))
            return null;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO links(id, remoteNestId, deviceId, key, folder, access, folders, ownerName, nestName, state, createdMs)
            VALUES($id, $nest, $dev, $key, $folder, $access, $folders, $owner, $name, 'New', $now)
            ON CONFLICT(id) DO UPDATE SET remoteNestId = $nest, deviceId = $dev, key = $key, folder = $folder, access = $access,
                folders = $folders, ownerName = $owner, nestName = $name, state = 'New', lastError = NULL;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$nest", remoteNestId);
        cmd.Parameters.AddWithValue("$dev", deviceId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$folder", folder);
        cmd.Parameters.AddWithValue("$access", access.ToString());
        cmd.Parameters.AddWithValue("$folders", folders is null ? (object)DBNull.Value : JsonSerializer.Serialize(folders));
        cmd.Parameters.AddWithValue("$owner", ownerName);
        cmd.Parameters.AddWithValue("$name", nestName);
        cmd.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        return Get(id);
    }

    /// <summary>The owner changed what the link may do or which folders it shares.</summary>
    public bool Change(string id, LinkAccess access, IReadOnlyList<string>? folders) =>
        Update(id, "access = $a, folders = $f", ("$a", access.ToString()), ("$f", folders is null ? (object)DBNull.Value : JsonSerializer.Serialize(folders)));

    /// <summary>Ends a link: the key is forgotten at once; the folder and its files stay.</summary>
    public bool Unlink(string id) => Update(id, "key = NULL, state = 'Unlinked'");

    /// <summary>A pass finished (<paramref name="error"/> null) or stopped; the cursors only move after a finished pass.</summary>
    public void Record(string id, LinkState state, string? error, string? remoteServerId = null, long? remoteCursor = null, long? localCursor = null)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        Update(id, """
            state = CASE WHEN state = 'Unlinked' THEN state ELSE $s END, lastError = $e,
            lastInStepMs = CASE WHEN $s = 'InStep' THEN $now ELSE lastInStepMs END,
            remoteServerId = COALESCE($sid, remoteServerId), remoteCursor = COALESCE($rc, remoteCursor), localCursor = COALESCE($lc, localCursor)
            """,
            ("$s", state.ToString()), ("$e", (object?)error ?? DBNull.Value), ("$now", now), ("$sid", (object?)remoteServerId ?? DBNull.Value),
            ("$rc", (object?)remoteCursor ?? DBNull.Value), ("$lc", (object?)localCursor ?? DBNull.Value));
    }

    /// <summary>The other nest is a new one (another server id): start over, comparing every file.</summary>
    public void Restart(string id)
    {
        Update(id, "remoteServerId = NULL, remoteCursor = 0");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM linkFiles WHERE linkId = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public LinkFile? GetFile(string linkId, string path)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, remoteHash, localHash FROM linkFiles WHERE linkId = $id AND path = $p;";
        cmd.Parameters.AddWithValue("$id", linkId);
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new LinkFile(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)) : null;
    }

    public void SetFile(string linkId, string path, string? remoteHash, string? localHash)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO linkFiles(linkId, path, remoteHash, localHash) VALUES($id, $p, $r, $l)
            ON CONFLICT(linkId, path) DO UPDATE SET remoteHash = $r, localHash = $l;
            """;
        cmd.Parameters.AddWithValue("$id", linkId);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$r", (object?)remoteHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", (object?)localHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void ForgetFile(string linkId, string path)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM linkFiles WHERE linkId = $id AND path = $p;";
        cmd.Parameters.AddWithValue("$id", linkId);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    private bool Update(string id, string set, params (string Name, object Value)[] values)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE links SET {set} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        foreach (var (name, value) in values)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery() == 1;
    }

    private static NestLink Read(SqliteDataReader r)
    {
        string? S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
        long? L(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));
        var folders = S("folders") is { } json ? JsonSerializer.Deserialize<List<string>>(json) : null;
        return new NestLink(
            S("id")!, S("remoteNestId")!, S("deviceId")!, S("key"), S("folder")!,
            Enum.TryParse<LinkAccess>(S("access"), out var a) ? a : LinkAccess.View,
            folders, S("ownerName")!, S("nestName")!,
            Enum.TryParse<LinkState>(S("state"), out var s) ? s : LinkState.New,
            S("remoteServerId"), L("remoteCursor") ?? 0, L("localCursor") ?? 0,
            DateTimeOffset.FromUnixTimeMilliseconds(L("createdMs") ?? 0),
            L("lastInStepMs") is { } ok ? DateTimeOffset.FromUnixTimeMilliseconds(ok) : null,
            S("lastError"));
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
