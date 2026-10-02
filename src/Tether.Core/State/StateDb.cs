using Microsoft.Data.Sqlite;

namespace Tether.Core.State;

/// <summary>Per-path client state: the agreed base hash plus a cache to avoid re-hashing unchanged files.</summary>
public sealed record FileState(string Path, string? BaseHash, long Size, long MtimeTicks, string? Hash, long HashedAtTicks)
{
    /// <summary>
    /// The cached hash may be reused only if size and mtime are unchanged AND the file was last
    /// modified well before it was hashed ("racy git" rule). Without the second condition, two
    /// same-size edits inside one timestamp tick could hide a change.
    /// </summary>
    public bool CacheValidFor(long size, long mtimeTicks, TimeSpan racyWindow) =>
        Hash is not null
        && Size == size
        && MtimeTicks == mtimeTicks
        && mtimeTicks < HashedAtTicks - racyWindow.Ticks;
}

/// <summary>A path the user must look at (name rejected by the server, case collision, ...).</summary>
public sealed record PathWarning(string Path, string Code, string? Message, string? LocalHash, long ServerCursor, long CreatedMs);

/// <summary>Which side a planned deletion applies to.</summary>
public enum DeleteSide
{
    Local,
    Remote,
}

/// <summary>
/// SQLite state database for one sync folder (one per folder, under %LocalAppData%\Tether).
/// Thread-safe: every call takes a short lock.
/// </summary>
public sealed class StateDb : IDisposable
{
    private const int SchemaVersion = 1;

    public const string MetaMarker = "marker";
    public const string MetaServerId = "serverId";
    public const string MetaCursor = "cursor";
    public const string MetaFolder = "folder";
    public const string MetaApprovalArmed = "deleteApprovalArmed";

    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public StateDb(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=FULL;");
        Exec("PRAGMA busy_timeout=5000;");
        Migrate();
    }

    public string DatabasePath { get; }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (version > SchemaVersion)
            throw new InvalidOperationException($"State database {DatabasePath} was created by a newer Tether (schema {version}).");
        if (version == SchemaVersion)
            return;

        Exec("""
            CREATE TABLE IF NOT EXISTS files(
                path TEXT PRIMARY KEY NOT NULL,
                baseHash TEXT NULL,
                size INTEGER NOT NULL DEFAULT -1,
                mtimeTicks INTEGER NOT NULL DEFAULT 0,
                hash TEXT NULL,
                hashedAtTicks INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS remote(
                path TEXT PRIMARY KEY NOT NULL,
                hash TEXT NULL,
                size INTEGER NOT NULL,
                modifiedMs INTEGER NOT NULL,
                deleted INTEGER NOT NULL,
                version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS warnings(
                path TEXT PRIMARY KEY NOT NULL,
                code TEXT NOT NULL,
                message TEXT NULL,
                localHash TEXT NULL,
                serverCursor INTEGER NOT NULL,
                createdMs INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS pendingDeletes(
                side TEXT NOT NULL,
                path TEXT NOT NULL,
                PRIMARY KEY(side, path));
            CREATE TABLE IF NOT EXISTS approvedDeletes(
                side TEXT NOT NULL,
                path TEXT NOT NULL,
                PRIMARY KEY(side, path));
            CREATE TABLE IF NOT EXISTS meta(
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL);
            """);
        Exec($"PRAGMA user_version={SchemaVersion};");
    }

    // ---------------------------------------------------------------- meta

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT value FROM meta WHERE key=$k", ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_gate)
        {
            using var cmd = value is null
                ? Command("DELETE FROM meta WHERE key=$k", ("$k", key))
                : Command("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
            cmd.ExecuteNonQuery();
        }
    }

    public string? MarkerId
    {
        get => GetMeta(MetaMarker);
        set => SetMeta(MetaMarker, value);
    }

    public string? ServerId
    {
        get => GetMeta(MetaServerId);
        set => SetMeta(MetaServerId, value);
    }

    /// <summary>Highest server manifest version this client has mirrored.</summary>
    public long Cursor
    {
        get => long.TryParse(GetMeta(MetaCursor), out var v) ? v : 0;
        set => SetMeta(MetaCursor, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------- files

    public FileState? GetFile(string path)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT path, baseHash, size, mtimeTicks, hash, hashedAtTicks FROM files WHERE path=$p", ("$p", path));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadFile(r) : null;
        }
    }

    public Dictionary<string, FileState> LoadFiles()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, FileState>(StringComparer.Ordinal);
            using var cmd = Command("SELECT path, baseHash, size, mtimeTicks, hash, hashedAtTicks FROM files");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var f = ReadFile(r);
                result[f.Path] = f;
            }
            return result;
        }
    }

    /// <summary>Number of paths with an agreed base (the "tracked" files for the mass-delete guard).</summary>
    public int CountTracked()
    {
        lock (_gate)
            return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM files WHERE baseHash IS NOT NULL"));
    }

    /// <summary>Sets the base without touching the hash cache.</summary>
    public void SetBase(string path, string? baseHash)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO files(path, baseHash) VALUES($p, $b)
                ON CONFLICT(path) DO UPDATE SET baseHash=excluded.baseHash
                """, ("$p", path), ("$b", baseHash));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Updates the hash cache without touching the base.</summary>
    public void SetCache(string path, long size, long mtimeTicks, string hash, long hashedAtTicks)
    {
        lock (_gate)
            UpsertCacheLocked(path, size, mtimeTicks, hash, hashedAtTicks);
    }

    /// <summary>Updates many cache rows in one transaction (used after a scan).</summary>
    public void SetCaches(IEnumerable<(string Path, long Size, long MtimeTicks, string Hash, long HashedAtTicks)> rows)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var row in rows)
                UpsertCacheLocked(row.Path, row.Size, row.MtimeTicks, row.Hash, row.HashedAtTicks, tx);
            tx.Commit();
        }
    }

    private void UpsertCacheLocked(string path, long size, long mtimeTicks, string hash, long hashedAtTicks, SqliteTransaction? tx = null)
    {
        using var cmd = Command("""
            INSERT INTO files(path, size, mtimeTicks, hash, hashedAtTicks) VALUES($p, $s, $m, $h, $t)
            ON CONFLICT(path) DO UPDATE SET size=excluded.size, mtimeTicks=excluded.mtimeTicks,
                hash=excluded.hash, hashedAtTicks=excluded.hashedAtTicks
            """, ("$p", path), ("$s", size), ("$m", mtimeTicks), ("$h", hash), ("$t", hashedAtTicks));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets base and cache together (after a successful transfer).</summary>
    public void SetSynced(string path, string baseHash, long size, long mtimeTicks, string? cachedHash, long hashedAtTicks)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO files(path, baseHash, size, mtimeTicks, hash, hashedAtTicks) VALUES($p, $b, $s, $m, $h, $t)
                ON CONFLICT(path) DO UPDATE SET baseHash=excluded.baseHash, size=excluded.size,
                    mtimeTicks=excluded.mtimeTicks, hash=excluded.hash, hashedAtTicks=excluded.hashedAtTicks
                """, ("$p", path), ("$b", baseHash), ("$s", size), ("$m", mtimeTicks), ("$h", cachedHash), ("$t", hashedAtTicks));
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveFile(string path)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM files WHERE path=$p", ("$p", path));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Removes cache-only rows for paths that no longer exist locally.</summary>
    public void PruneCacheRows(IReadOnlySet<string> existingLocalPaths)
    {
        lock (_gate)
        {
            var stale = new List<string>();
            using (var cmd = Command("SELECT path FROM files WHERE baseHash IS NULL"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var p = r.GetString(0);
                    if (!existingLocalPaths.Contains(p))
                        stale.Add(p);
                }
            }
            if (stale.Count == 0)
                return;
            using var tx = _conn.BeginTransaction();
            foreach (var p in stale)
            {
                using var del = Command("DELETE FROM files WHERE path=$p AND baseHash IS NULL", ("$p", p));
                del.Transaction = tx;
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Forgets every base (keeps the hash cache). The next pass then merges like a first sync:
    /// nothing is deleted, differing files become conflict copies. Used after a server was replaced
    /// or restored from a backup.
    /// </summary>
    public void ClearAllBases()
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            Exec("UPDATE files SET baseHash=NULL", tx);
            Exec("DELETE FROM remote", tx);
            Exec("DELETE FROM warnings", tx);
            Exec("DELETE FROM pendingDeletes", tx);
            Exec("DELETE FROM approvedDeletes", tx);
            Exec($"DELETE FROM meta WHERE key IN ('{MetaServerId}','{MetaCursor}','{MetaApprovalArmed}')", tx);
            tx.Commit();
        }
    }

    // ---------------------------------------------------------------- remote mirror

    public Dictionary<string, ManifestEntry> LoadRemote()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            using var cmd = Command("SELECT path, hash, size, modifiedMs, deleted, version FROM remote");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var e = new ManifestEntry(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0, r.GetInt64(5));
                result[e.Path] = e;
            }
            return result;
        }
    }

    /// <summary>
    /// Applies a manifest response to the mirror. With <paramref name="replaceAll"/> the mirror is
    /// replaced (full fetch); otherwise entries are upserted (delta fetch). The cursor is stored in
    /// the same transaction so mirror and cursor never disagree.
    /// </summary>
    public void ApplyRemote(IReadOnlyCollection<ManifestEntry> entries, bool replaceAll, long cursor, string serverId)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            if (replaceAll)
                Exec("DELETE FROM remote", tx);
            foreach (var e in entries)
                UpsertRemoteLocked(e, tx);
            SetMetaLocked(MetaCursor, cursor.ToString(System.Globalization.CultureInfo.InvariantCulture), tx);
            SetMetaLocked(MetaServerId, serverId, tx);
            tx.Commit();
        }
    }

    /// <summary>Records an entry returned by an upload or delete (newer than the mirror).</summary>
    public void UpsertRemote(ManifestEntry entry)
    {
        lock (_gate)
            UpsertRemoteLocked(entry, null);
    }

    private void UpsertRemoteLocked(ManifestEntry e, SqliteTransaction? tx)
    {
        using var cmd = Command("""
            INSERT INTO remote(path, hash, size, modifiedMs, deleted, version) VALUES($p, $h, $s, $m, $d, $v)
            ON CONFLICT(path) DO UPDATE SET hash=excluded.hash, size=excluded.size, modifiedMs=excluded.modifiedMs,
                deleted=excluded.deleted, version=excluded.version
            WHERE excluded.version >= remote.version
            """, ("$p", e.Path), ("$h", e.Hash), ("$s", e.Size), ("$m", e.ModifiedMs), ("$d", e.Deleted ? 1 : 0), ("$v", e.Version));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- warnings

    public Dictionary<string, PathWarning> LoadWarnings()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, PathWarning>(StringComparer.Ordinal);
            using var cmd = Command("SELECT path, code, message, localHash, serverCursor, createdMs FROM warnings");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var w = new PathWarning(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5));
                result[w.Path] = w;
            }
            return result;
        }
    }

    public void SetWarning(PathWarning w)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO warnings(path, code, message, localHash, serverCursor, createdMs) VALUES($p, $c, $m, $h, $s, $t)
                ON CONFLICT(path) DO UPDATE SET code=excluded.code, message=excluded.message, localHash=excluded.localHash,
                    serverCursor=excluded.serverCursor, createdMs=excluded.createdMs
                """, ("$p", w.Path), ("$c", w.Code), ("$m", w.Message), ("$h", w.LocalHash), ("$s", w.ServerCursor), ("$t", w.CreatedMs));
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearWarning(string path)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM warnings WHERE path=$p", ("$p", path));
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- mass-delete approval

    /// <summary>Stores the deletions a blocked pass wanted to perform (replacing the previous set).</summary>
    public void SetPendingDeletes(IEnumerable<(DeleteSide Side, string Path)> deletes)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            Exec("DELETE FROM pendingDeletes", tx);
            foreach (var (side, path) in deletes)
            {
                using var cmd = Command("INSERT OR IGNORE INTO pendingDeletes(side, path) VALUES($s, $p)", ("$s", side.ToString()), ("$p", path));
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<(DeleteSide Side, string Path)> GetPendingDeletes() => ReadDeletes("pendingDeletes");

    /// <summary>
    /// "Allow these deletions": copies the pending set into the approved set and arms it for
    /// exactly one pass. Returns the number of approved deletions.
    /// </summary>
    public int ApprovePendingDeletes()
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            Exec("DELETE FROM approvedDeletes", tx);
            Exec("INSERT INTO approvedDeletes(side, path) SELECT side, path FROM pendingDeletes", tx);
            Exec("DELETE FROM pendingDeletes", tx);
            var count = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM approvedDeletes", tx));
            SetMetaLocked(MetaApprovalArmed, count > 0 ? "1" : "0", tx);
            tx.Commit();
            return count;
        }
    }

    /// <summary>Returns the armed approval (if any) and disarms it: an approval covers one pass only.</summary>
    public HashSet<(DeleteSide Side, string Path)>? ConsumeApproval()
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var armed = Scalar($"SELECT value FROM meta WHERE key='{MetaApprovalArmed}'", tx) as string == "1";
            HashSet<(DeleteSide, string)>? result = null;
            if (armed)
            {
                result = [];
                using var cmd = Command("SELECT side, path FROM approvedDeletes");
                cmd.Transaction = tx;
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        result.Add((Enum.Parse<DeleteSide>(r.GetString(0)), r.GetString(1)));
                }
            }
            Exec("DELETE FROM approvedDeletes", tx);
            SetMetaLocked(MetaApprovalArmed, "0", tx);
            tx.Commit();
            return result;
        }
    }

    public void ClearPendingDeletes()
    {
        lock (_gate)
            Exec("DELETE FROM pendingDeletes");
    }

    private List<(DeleteSide, string)> ReadDeletes(string table)
    {
        lock (_gate)
        {
            var result = new List<(DeleteSide, string)>();
            using var cmd = Command($"SELECT side, path FROM {table} ORDER BY side, path");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((Enum.Parse<DeleteSide>(r.GetString(0)), r.GetString(1)));
            return result;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static FileState ReadFile(SqliteDataReader r) =>
        new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5));

    private void SetMetaLocked(string key, string value, SqliteTransaction tx)
    {
        using var cmd = Command("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Command(sql);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Command(sql);
        cmd.Transaction = tx;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _conn.Dispose();
        }
    }
}
