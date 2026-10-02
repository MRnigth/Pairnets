using Tether.Core;
using Tether.Core.Hashing;
using Tether.Core.Paths;

namespace Tether.Server.Storage;

/// <summary>
/// The server's file store. Every change runs under one global lock: compare the client's base
/// with the current state, move the old version into history/, atomically rename the new file
/// into files/, and commit the manifest. Nothing is ever destroyed outside the history purge.
/// </summary>
public sealed class SyncStore : IDisposable
{
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CaseIndex _caseIndex = new();
    private FileStream? _dataDirLock;
    private bool _initialized;

    public SyncStore(ServerPaths paths, ILogger<SyncStore> log, TimeProvider? clock = null)
    {
        Paths = paths;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        paths.EnsureCreated();
        Manifest = new ManifestStore(paths.Database);
    }

    public ServerPaths Paths { get; }

    public ManifestStore Manifest { get; }

    public string ServerId => Manifest.ServerId;

    public long CurrentVersion => Manifest.CurrentVersion;

    /// <summary>
    /// Takes the exclusive data-dir lock (so CLI maintenance cannot run concurrently), finishes or
    /// rolls back interrupted changes, empties tmp/ and builds the case index.
    /// </summary>
    public void Initialize(bool takeLock = true)
    {
        if (_initialized)
            return;
        if (takeLock)
            _dataDirLock = AcquireDataDirLock(Paths);
        RecoverJournal();
        CleanTmp();
        foreach (var entry in Manifest.AllLive())
            _caseIndex.Add(entry.Path);
        _initialized = true;
        _log.LogInformation("Store ready: {Files} live file(s), version {Version}, data dir {DataDir}",
            _caseIndex.FileCount, Manifest.CurrentVersion, Paths.DataDir);
    }

    /// <summary>Opens DataDir/.lock exclusively. Throws IOException when another process holds it.</summary>
    public static FileStream AcquireDataDirLock(ServerPaths paths)
    {
        try
        {
            return new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException($"The data directory {paths.DataDir} is in use by another tether-server process. Stop the service first (sudo systemctl stop tether-server).", ex);
        }
    }

    // ------------------------------------------------------------------ reads

    public (List<ManifestEntry> Entries, long Version) ReadManifest(long? since) => Manifest.Read(since);

    /// <summary>Looks up the entry and opens its file as one consistent pair. Null when absent or deleted.</summary>
    public async Task<OpenedFile?> OpenReadAsync(string path, CancellationToken ct)
    {
        if (!PathRules.IsValid(path))
            return null;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Manifest.Get(path);
            if (entry is null || entry.Deleted)
                return null;
            var full = ResolveFilesPath(path);
            if (full is null || !File.Exists(full))
            {
                _log.LogWarning("Manifest lists {Path} but the file is missing from files/ (run 'tether-server rescan')", path);
                return null;
            }
            var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new OpenedFile(entry, stream);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Validates a "base" query value: "none" or a SHA-256 hex hash.</summary>
    public static bool TryParseBase(string? value, out string? baseHash)
    {
        baseHash = null;
        if (value == ContentHash.NoneBase)
            return true;
        if (ContentHash.IsValid(value))
        {
            baseHash = value;
            return true;
        }
        return false;
    }

    /// <summary>Quick check before receiving a body: would this change be rejected anyway?</summary>
    public async Task<ChangeResult?> PrecheckAsync(string path, string? expectedBase, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var collision = _caseIndex.FindCollision(path);
            if (collision is not null)
                return new ChangeResult(ChangeStatus.CaseCollision, null, collision);
            var current = LiveHash(Manifest.Get(path));
            if (!string.Equals(current, expectedBase, StringComparison.Ordinal))
                return new ChangeResult(ChangeStatus.Conflict, null, "The file changed on the server since your last sync.");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Streams <paramref name="body"/> into tmp/ and commits it if the base still matches.</summary>
    public async Task<ChangeResult> PutAsync(string path, string baseValue, long? mtimeMs, Stream body, CancellationToken ct)
    {
        var problem = PathRules.Check(path);
        if (problem != PathProblem.None)
            return new ChangeResult(ChangeStatus.InvalidName, null, $"Invalid path: {problem}.");
        if (!TryParseBase(baseValue, out var expectedBase))
            return new ChangeResult(ChangeStatus.BadRequest, null, "base must be 'none' or a SHA-256 hash.");

        var pre = await PrecheckAsync(path, expectedBase, ct).ConfigureAwait(false);
        if (pre is not null)
            return pre;

        var tmp = NewTmpPath(".upload");
        try
        {
            string hash;
            long size;
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await using var hashing = new HashingStream(fs, leaveOpen: true);
                await body.CopyToAsync(hashing, 1024 * 1024, ct).ConfigureAwait(false);
                await hashing.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                hash = hashing.GetHash();
                size = hashing.BytesTransferred;
                fs.Flush(flushToDisk: true);
            }
            var modified = mtimeMs ?? _clock.GetUtcNow().ToUnixTimeMilliseconds();
            return await CommitFileAsync(path, tmp, hash, size, modified,
                current => string.Equals(current, expectedBase, StringComparison.Ordinal)).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Moves the current file into history/ and writes a tombstone, if the base matches.</summary>
    public async Task<ChangeResult> DeleteAsync(string path, string baseValue)
    {
        var problem = PathRules.Check(path);
        if (problem != PathProblem.None)
            return new ChangeResult(ChangeStatus.InvalidName, null, $"Invalid path: {problem}.");
        if (!TryParseBase(baseValue, out var expectedBase))
            return new ChangeResult(ChangeStatus.BadRequest, null, "base must be 'none' or a SHA-256 hash.");

        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = Manifest.Get(path);
            if (current is null)
                return new ChangeResult(ChangeStatus.NotFound, null, "No such file.");
            if (current.Deleted)
                return new ChangeResult(ChangeStatus.Unchanged, current);
            if (!string.Equals(current.Hash, expectedBase, StringComparison.Ordinal))
                return new ChangeResult(ChangeStatus.Conflict, null, "The file changed on the server since your last sync.");

            var full = ResolveFilesPath(path) ?? throw new IOException($"Unsafe path {path}.");
            var historyRel = File.Exists(full) ? NewHistoryRelative(path, current.Hash!) : null;
            var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var journalId = Manifest.AddJournal("delete", path, null, historyRel, null, 0, now);
            if (historyRel is not null)
            {
                var historyFull = Path.Combine(Paths.History, historyRel);
                Directory.CreateDirectory(Path.GetDirectoryName(historyFull)!);
                File.Move(full, historyFull);
            }
            else
            {
                _log.LogWarning("Deleting {Path}: file was already missing from files/", path);
            }
            var entry = Manifest.Commit(path, null, 0, now, deleted: true, journalId);
            _caseIndex.Remove(path);
            PruneEmptyFolders(Paths.Files, Path.GetDirectoryName(full)!);
            _log.LogInformation("Deleted {Path} (version {Version})", path, entry.Version);
            return new ChangeResult(ChangeStatus.Ok, entry);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Restores a history version as a normal new version of the file.</summary>
    public async Task<ChangeResult> RestoreAsync(string path, string id, CancellationToken ct)
    {
        if (!PathRules.IsValid(path))
            return new ChangeResult(ChangeStatus.InvalidName, null, "Invalid path.");
        if (!HistoryNames.IsValidId(id))
            return new ChangeResult(ChangeStatus.BadRequest, null, "Invalid history id.");
        var source = HistoryFilePath(path, id);
        if (source is null || !File.Exists(source))
            return new ChangeResult(ChangeStatus.NotFound, null, "No such history version.");

        var tmp = NewTmpPath(".restore");
        try
        {
            string hash;
            long size;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous))
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await using var hashing = new HashingStream(output, leaveOpen: true);
                await input.CopyToAsync(hashing, ct).ConfigureAwait(false);
                hash = hashing.GetHash();
                size = hashing.BytesTransferred;
                output.Flush(flushToDisk: true);
            }
            var result = await CommitFileAsync(path, tmp, hash, size, _clock.GetUtcNow().ToUnixTimeMilliseconds(), _ => true).ConfigureAwait(false);
            if (result.Changed)
                _log.LogInformation("Restored {Path} from history version {Id}", path, id);
            return result;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Adds or updates a file that already sits in files/ (rescan). Caller holds no lock.</summary>
    internal async Task<ChangeResult> AdoptExistingFileAsync(string path, string hash, long size, long modifiedMs)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var collision = _caseIndex.FindCollision(path);
            if (collision is not null)
                return new ChangeResult(ChangeStatus.CaseCollision, null, collision);
            var current = Manifest.Get(path);
            if (current is { Deleted: false } && current.Hash == hash)
                return new ChangeResult(ChangeStatus.Unchanged, current);
            var entry = Manifest.Commit(path, hash, size, modifiedMs, deleted: false, journalId: null);
            _caseIndex.Add(path);
            return new ChangeResult(ChangeStatus.Ok, entry);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ChangeResult> CommitFileAsync(string path, string tmp, string hash, long size, long modifiedMs, Func<string?, bool> baseMatches)
    {
        // Once the content is fully received, the commit runs to completion even if the client goes away.
        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var collision = _caseIndex.FindCollision(path);
            if (collision is not null)
                return new ChangeResult(ChangeStatus.CaseCollision, null, collision);

            var current = Manifest.Get(path);
            var currentHash = LiveHash(current);
            if (string.Equals(currentHash, hash, StringComparison.Ordinal))
                return new ChangeResult(ChangeStatus.Unchanged, current);
            if (!baseMatches(currentHash))
                return new ChangeResult(ChangeStatus.Conflict, null, "The file changed on the server since your last sync.");

            var full = ResolveFilesPath(path) ?? throw new IOException($"Unsafe path {path}.");
            string? historyRel = null;
            if (File.Exists(full))
            {
                // Keep whatever is there, even a file added by hand that the manifest does not know.
                var oldHash = currentHash ?? (await ContentHash.OfFileAsync(full).ConfigureAwait(false)).Hash;
                historyRel = NewHistoryRelative(path, oldHash);
            }
            var journalId = Manifest.AddJournal("put", path, Path.GetFileName(tmp), historyRel, hash, size, modifiedMs);

            string? historyFull = null;
            try
            {
                if (historyRel is not null)
                {
                    historyFull = Path.Combine(Paths.History, historyRel);
                    Directory.CreateDirectory(Path.GetDirectoryName(historyFull)!);
                    File.Move(full, historyFull);
                }
                var parent = Path.GetDirectoryName(full)!;
                if (File.Exists(parent))
                    throw new IOException($"A file exists where the folder for {path} should be.");
                Directory.CreateDirectory(parent);
                File.Move(tmp, full);
            }
            catch
            {
                // Roll back: put the previous version back where it was.
                if (historyFull is not null && File.Exists(historyFull) && !File.Exists(full))
                    File.Move(historyFull, full);
                Manifest.RemoveJournal(journalId);
                throw;
            }

            TrySetMtime(full, modifiedMs);
            var entry = Manifest.Commit(path, hash, size, modifiedMs, deleted: false, journalId);
            _caseIndex.Add(path);
            _log.LogInformation("Stored {Path} ({Size} bytes, version {Version})", path, size, entry.Version);
            return new ChangeResult(ChangeStatus.Ok, entry);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------------ history

    public List<HistoryVersion> ListHistory(string path)
    {
        var dir = HistoryDirectory(path);
        var list = new List<HistoryVersion>();
        if (dir is null || !Directory.Exists(dir))
            return list;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            if (!HistoryNames.TryParse(file.Name, out var storedAt, out var hash8))
                continue;
            list.Add(new HistoryVersion(file.Name, storedAt, file.Length, hash8));
        }
        list.Sort((a, b) => b.StoredAtUtc.CompareTo(a.StoredAtUtc));
        return list;
    }

    /// <summary>
    /// Deletes history versions older than the retention period, always keeping the newest
    /// <paramref name="minVersions"/> versions of every file.
    /// </summary>
    public async Task<(int Deleted, long Bytes)> PurgeHistoryAsync(TimeSpan retention, int minVersions, bool dryRun, CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - retention;
        var deleted = 0;
        long bytes = 0;
        var directories = new List<string>();
        CollectDirectories(Paths.History, directories);
        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();
            var versions = new List<(FileInfo File, DateTimeOffset StoredAt)>();
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && HistoryNames.TryParse(file.Name, out var at, out _))
                    versions.Add((file, at));
            }
            versions.Sort((a, b) => b.StoredAt.CompareTo(a.StoredAt));
            var expired = versions.Skip(minVersions).Where(v => v.StoredAt < cutoff).ToList();
            if (expired.Count == 0)
                continue;
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var (file, _) in expired)
                {
                    bytes += file.Length;
                    deleted++;
                    if (!dryRun)
                        TryDelete(file.FullName);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
        if (!dryRun)
            PruneEmptyTree(Paths.History);
        if (deleted > 0)
            _log.LogInformation("History purge {Mode}: {Count} version(s), {Bytes} bytes", dryRun ? "(dry run) would delete" : "deleted", deleted, bytes);
        return (deleted, bytes);
    }

    private static void CollectDirectories(string root, List<string> into)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            into.Add(dir);
            foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    stack.Push(sub.FullName);
            }
        }
    }

    // ------------------------------------------------------------------ recovery and drift

    private void RecoverJournal()
    {
        foreach (var j in Manifest.PendingJournal())
        {
            var full = ResolveFilesPath(j.Path);
            var historyFull = j.HistoryFile is null ? null : Path.Combine(Paths.History, j.HistoryFile);
            if (full is null)
            {
                Manifest.RemoveJournal(j.Id);
                continue;
            }

            if (j.Op is "put")
            {
                var tmp = j.TmpFile is null ? null : Path.Combine(Paths.Tmp, j.TmpFile);
                if (tmp is not null && File.Exists(tmp))
                {
                    // The new file never reached files/: roll back.
                    if (historyFull is not null && File.Exists(historyFull) && !File.Exists(full))
                        File.Move(historyFull, full);
                    Manifest.RemoveJournal(j.Id);
                    _log.LogWarning("Recovered interrupted upload of {Path}: rolled back", j.Path);
                }
                else if (File.Exists(full) && j.Hash is not null)
                {
                    // The new file is in place but the manifest was not updated: roll forward.
                    TrySetMtime(full, j.ModifiedMs);
                    Manifest.Commit(j.Path, j.Hash, j.Size, j.ModifiedMs, deleted: false, j.Id);
                    _log.LogWarning("Recovered interrupted upload of {Path}: completed", j.Path);
                }
                else
                {
                    if (historyFull is not null && File.Exists(historyFull) && !File.Exists(full))
                        File.Move(historyFull, full);
                    Manifest.RemoveJournal(j.Id);
                    _log.LogError("Interrupted upload of {Path} could not be recovered; previous version kept", j.Path);
                }
            }
            else if (j.Op is "delete")
            {
                if (!File.Exists(full))
                {
                    Manifest.Commit(j.Path, null, 0, j.ModifiedMs, deleted: true, j.Id);
                    _log.LogWarning("Recovered interrupted delete of {Path}: completed", j.Path);
                }
                else
                {
                    Manifest.RemoveJournal(j.Id);
                    _log.LogWarning("Recovered interrupted delete of {Path}: rolled back", j.Path);
                }
            }
            else
            {
                Manifest.RemoveJournal(j.Id);
            }
        }
    }

    private void CleanTmp()
    {
        foreach (var file in Directory.EnumerateFiles(Paths.Tmp))
            TryDelete(file);
    }

    /// <summary>Compares files/ with the manifest. Never changes anything.</summary>
    public DriftReport DetectDrift()
    {
        var report = new DriftReport();
        var live = Manifest.AllLive().ToDictionary(e => e.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(string Full, string Rel)>();
        stack.Push((Paths.Files, string.Empty));
        while (stack.Count > 0)
        {
            var (dirFull, dirRel) = stack.Pop();
            foreach (var entry in new DirectoryInfo(dirFull).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                var rel = dirRel.Length == 0 ? entry.Name : dirRel + "/" + entry.Name;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                {
                    report.Symlinks.Add(rel);
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    stack.Push((entry.FullName, rel));
                    continue;
                }
                if (!PathRules.IsValid(rel))
                {
                    report.InvalidNames.Add(rel);
                    continue;
                }
                seen.Add(rel);
                if (!live.TryGetValue(rel, out var e))
                    report.Unknown.Add(rel);
                else if (((FileInfo)entry).Length != e.Size)
                    report.SizeMismatch.Add(rel);
            }
        }
        foreach (var path in live.Keys)
        {
            if (!seen.Contains(path))
                report.Missing.Add(path);
        }
        return report;
    }

    // ------------------------------------------------------------------ helpers

    private static string? LiveHash(ManifestEntry? entry) => entry is { Deleted: false } ? entry.Hash : null;

    /// <summary>Resolves a sync path under files/, refusing escapes and symlinked components.</summary>
    public string? ResolveFilesPath(string path)
    {
        var full = PathRules.ResolveUnder(Paths.Files, path);
        if (full is null || PathRules.HasReparsePoint(Paths.Files, path))
            return null;
        return full;
    }

    private string? HistoryDirectory(string path)
    {
        var dir = PathRules.ResolveUnder(Paths.History, path);
        if (dir is null || PathRules.HasReparsePoint(Paths.History, path))
            return null;
        return dir;
    }

    private string? HistoryFilePath(string path, string id)
    {
        var dir = HistoryDirectory(path);
        if (dir is null)
            return null;
        var file = Path.Combine(dir, id);
        return File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 ? file : null;
    }

    /// <summary>history-relative path for a new version of <paramref name="path"/>, unique by timestamp.</summary>
    private string NewHistoryRelative(string path, string hash)
    {
        var dir = HistoryDirectory(path) ?? throw new IOException($"Unsafe history path for {path}.");
        var at = _clock.GetUtcNow();
        while (true)
        {
            var id = HistoryNames.MakeId(at, ContentHash.Short(hash));
            if (!File.Exists(Path.Combine(dir, id)))
                return Path.GetRelativePath(Paths.History, Path.Combine(dir, id));
            at = at.AddMilliseconds(1);
        }
    }

    private string NewTmpPath(string suffix) => Path.Combine(Paths.Tmp, Guid.NewGuid().ToString("N") + suffix);

    private void TrySetMtime(string full, long modifiedMs)
    {
        try
        {
            File.SetLastWriteTimeUtc(full, DateTimeOffset.FromUnixTimeMilliseconds(modifiedMs).UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or UnauthorizedAccessException)
        {
            _log.LogDebug("Could not set mtime of {File}: {Error}", full, ex.Message);
        }
    }

    private void PruneEmptyFolders(string root, string start)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
        while (dir.Length > rootFull.Length && dir.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    return;
                Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    private void PruneEmptyTree(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not delete {File}: {Error}", file, ex.Message);
        }
    }

    public void Dispose()
    {
        _dataDirLock?.Dispose();
        _dataDirLock = null;
        _lock.Dispose();
    }
}

/// <summary>Differences between files/ and the manifest.</summary>
public sealed class DriftReport
{
    public List<string> Unknown { get; } = [];
    public List<string> Missing { get; } = [];
    public List<string> SizeMismatch { get; } = [];
    public List<string> InvalidNames { get; } = [];
    public List<string> Symlinks { get; } = [];

    public bool IsClean => Unknown.Count == 0 && Missing.Count == 0 && SizeMismatch.Count == 0 && InvalidNames.Count == 0 && Symlinks.Count == 0;
}
