using System.Globalization;
using Microsoft.Data.Sqlite;
using Tether.Core;
using Tether.Core.Paths;

namespace Tether.Server.Storage;

/// <summary>A change that was started but not yet committed (used for crash recovery).</summary>
public sealed record JournalEntry(long Id, string Op, string Path, string? TmpFile, string? HistoryFile, string? Hash, long Size, long ModifiedMs);

/// <summary>
/// SQLite manifest: one row per path with a global monotonic version, plus a journal that makes
/// the multi-step file moves recoverable after a crash.
/// </summary>
public sealed class ManifestStore
{
    private readonly string _connectionString;

    public ManifestStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > 1)
            throw new InvalidOperationException($"manifest.db schema {version} is newer than this server supports.");
        if (version == 0)
        {
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS files(
                    path TEXT PRIMARY KEY NOT NULL,
                    pathLower TEXT NOT NULL,
                    hash TEXT NULL,
                    size INTEGER NOT NULL,
                    modifiedMs INTEGER NOT NULL,
                    deleted INTEGER NOT NULL,
                    version INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_files_lower ON files(pathLower);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_files_version ON files(version);
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS journal(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    op TEXT NOT NULL,
                    path TEXT NOT NULL,
                    tmpFile TEXT NULL,
                    historyFile TEXT NULL,
                    hash TEXT NULL,
                    size INTEGER NOT NULL,
                    modifiedMs INTEGER NOT NULL);
                PRAGMA user_version=1;
                """);
        }

        ServerId = Scalar(conn, "SELECT value FROM meta WHERE key='serverId'") as string ?? CreateServerId(conn);
    }

    /// <summary>Random identity of this data directory; clients notice when it changes.</summary>
    public string ServerId { get; }

    private static string CreateServerId(SqliteConnection conn)
    {
        var id = Guid.NewGuid().ToString("D");
        using var cmd = Command(conn, "INSERT INTO meta(key, value) VALUES('serverId', $v)", ("$v", id));
        cmd.ExecuteNonQuery();
        return id;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Exec(conn, "PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;");
        return conn;
    }

    public long CurrentVersion
    {
        get
        {
            using var conn = Open();
            return ReadVersion(conn, null);
        }
    }

    public ManifestEntry? Get(string path)
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT path, hash, size, modifiedMs, deleted, version FROM files WHERE path=$p", ("$p", path));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEntry(r) : null;
    }

    /// <summary>Entries changed after <paramref name="since"/> (all when null) and the version they were read at, as one snapshot.</summary>
    public (List<ManifestEntry> Entries, long Version) Read(long? since)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(deferred: true);
        var version = ReadVersion(conn, tx);
        var list = new List<ManifestEntry>();
        using var cmd = since is null
            ? Command(conn, "SELECT path, hash, size, modifiedMs, deleted, version FROM files ORDER BY version")
            : Command(conn, "SELECT path, hash, size, modifiedMs, deleted, version FROM files WHERE version > $v ORDER BY version", ("$v", since.Value));
        cmd.Transaction = tx;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                list.Add(ReadEntry(r));
        }
        tx.Commit();
        return (list, version);
    }

    public List<ManifestEntry> AllLive()
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT path, hash, size, modifiedMs, deleted, version FROM files WHERE deleted=0");
        using var r = cmd.ExecuteReader();
        var list = new List<ManifestEntry>();
        while (r.Read())
            list.Add(ReadEntry(r));
        return list;
    }

    public long AddJournal(string op, string path, string? tmpFile, string? historyFile, string? hash, long size, long modifiedMs)
    {
        using var conn = Open();
        using var cmd = Command(conn, """
            INSERT INTO journal(op, path, tmpFile, historyFile, hash, size, modifiedMs) VALUES($o, $p, $t, $h, $x, $s, $m);
            SELECT last_insert_rowid();
            """, ("$o", op), ("$p", path), ("$t", tmpFile), ("$h", historyFile), ("$x", hash), ("$s", size), ("$m", modifiedMs));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public List<JournalEntry> PendingJournal()
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT id, op, path, tmpFile, historyFile, hash, size, modifiedMs FROM journal ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<JournalEntry>();
        while (r.Read())
        {
            list.Add(new JournalEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), r.GetInt64(7)));
        }
        return list;
    }

    public void RemoveJournal(long id)
    {
        using var conn = Open();
        using var cmd = Command(conn, "DELETE FROM journal WHERE id=$i", ("$i", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes a new state for a path with the next global version, and removes the journal row,
    /// in one transaction.
    /// </summary>
    public ManifestEntry Commit(string path, string? hash, long size, long modifiedMs, bool deleted, long? journalId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var version = ReadVersion(conn, tx) + 1;
        using (var cmd = Command(conn, """
            INSERT INTO files(path, pathLower, hash, size, modifiedMs, deleted, version) VALUES($p, $l, $h, $s, $m, $d, $v)
            ON CONFLICT(path) DO UPDATE SET hash=excluded.hash, size=excluded.size, modifiedMs=excluded.modifiedMs,
                deleted=excluded.deleted, version=excluded.version
            """, ("$p", path), ("$l", PathRules.CaseKey(path)), ("$h", hash), ("$s", size), ("$m", modifiedMs), ("$d", deleted ? 1 : 0), ("$v", version)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = Command(conn, "INSERT INTO meta(key, value) VALUES('version', $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            ("$v", version.ToString(CultureInfo.InvariantCulture))))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        if (journalId is not null)
        {
            using var cmd = Command(conn, "DELETE FROM journal WHERE id=$i", ("$i", journalId.Value));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return new ManifestEntry(path, hash, size, modifiedMs, deleted, version);
    }

    /// <summary>Copies the database consistently (used by tests and documented for backups).</summary>
    public void BackupTo(string destinationPath)
    {
        using var conn = Open();
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        dest.Open();
        conn.BackupDatabase(dest);
    }

    private static long ReadVersion(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = Command(conn, "SELECT value FROM meta WHERE key='version'");
        cmd.Transaction = tx;
        return cmd.ExecuteScalar() is string s ? long.Parse(s, CultureInfo.InvariantCulture) : 0;
    }

    private static ManifestEntry ReadEntry(SqliteDataReader r) =>
        new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0, r.GetInt64(5));

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

    /// <summary>Releases pooled connections so the files can be deleted or copied.</summary>
    public static void ReleasePools() => SqliteConnection.ClearAllPools();
}
