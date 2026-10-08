using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Pairnets.Core;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;

namespace Pairnets.Server.Storage;

/// <summary>
/// Uploads that arrive in pieces (POST /api/upload, PUT /api/upload/{id}, POST /api/upload/{id}/commit).
/// A file of any size then fits through a proxy that caps one request (Cloudflare: 100 MB), and an
/// upload cut off half-way continues where it stopped instead of starting again. Every byte the server
/// takes in is written, hashed and counted together, so "received" is always exactly what the part file
/// holds and the client can resume from it.
/// <para>
/// A session is its part file in tmp/ (<c>id.part</c>, written to disk after every piece) plus a small
/// description next to it (<c>id.session</c>: path, base, size, mtime). Both survive a restart of the server:
/// the running hash of what arrived lived only in memory, so after a restart each kept part file is read and
/// hashed once more in the background, and the upload answers "busy" meanwhile. The commit still compares
/// the client's hash of the whole file with the server's hash of the bytes it has, so a part file that is not
/// exactly what the client sent is refused, never stored.
/// </para>
/// </summary>
public sealed class UploadSessions : IDisposable
{
    /// <summary>Largest piece the server takes in one request (the client sends 50 MiB).</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;

    public const int MaxSessions = 64;

    /// <summary>A session that receives nothing for this long is dropped with its part file.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    /// <summary>The part file of a session in tmp/: what arrived so far.</summary>
    public const string PartSuffix = ".part";

    /// <summary>The description of a session in tmp/, so the session can be taken up again after a restart.</summary>
    public const string InfoSuffix = ".session";

    private readonly SyncStore _store;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Takes up the uploads kept over a restart; the store must be initialized first (it cleans tmp/).</summary>
    public UploadSessions(SyncStore store, ILogger<UploadSessions> log, TimeProvider? clock = null)
    {
        _store = store;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        RestoreKept();
    }

    /// <summary>True for the file names of sessions in tmp/ (<c>id.part</c>, <c>id.session</c>), with the id.</summary>
    public static bool TryParseFileName(string name, out string id)
    {
        id = string.Empty;
        string stem;
        if (name.EndsWith(PartSuffix, StringComparison.Ordinal))
            stem = name[..^PartSuffix.Length];
        else if (name.EndsWith(InfoSuffix, StringComparison.Ordinal))
            stem = name[..^InfoSuffix.Length];
        else
            return false;
        if (stem.Length != 32 || !stem.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            return false;
        id = stem;
        return true;
    }

    public int Count => _sessions.Count;

    public enum StartStatus { Started, Rejected, TooMany }

    /// <summary><see cref="ChunkStatus.Busy"/>: an earlier piece of the same upload is still arriving; the client waits a moment.</summary>
    public enum ChunkStatus { Ok, NotFound, WrongOffset, TooMuchData, Stalled, Busy }

    public enum CommitStatus { Done, NotFound, Incomplete, HashMismatch, Busy }

    public sealed record StartResult(StartStatus Status, string? Id = null, ChangeResult? Rejection = null);

    public sealed record ChunkResult(ChunkStatus Status, long Received = 0);

    public sealed record CommitResult(CommitStatus Status, ChangeResult? Change = null, string? Message = null);

    /// <summary>Checks the change like a single PUT would (name, base, collisions) and opens a session.</summary>
    public async Task<StartResult> StartAsync(string path, string baseValue, long size, long? mtimeMs, CancellationToken ct)
    {
        var problem = PathRules.Check(path);
        if (problem != PathProblem.None)
            return new(StartStatus.Rejected, Rejection: new ChangeResult(ChangeStatus.InvalidName, null, $"Invalid path: {problem}."));
        if (!SyncStore.TryParseBase(baseValue, out var expectedBase))
            return new(StartStatus.Rejected, Rejection: new ChangeResult(ChangeStatus.BadRequest, null, "base must be 'none' or a SHA-256 hash."));
        if (size < 0)
            return new(StartStatus.Rejected, Rejection: new ChangeResult(ChangeStatus.BadRequest, null, "size must be a non-negative integer."));
        var pre = await _store.PrecheckAsync(path, expectedBase, ct).ConfigureAwait(false);
        if (pre is not null)
            return new(StartStatus.Rejected, Rejection: pre);

        ExpireIdle();
        if (_sessions.Count >= MaxSessions)
            return new(StartStatus.TooMany);

        var id = NewId();
        var session = new Session(id, path, expectedBase, size, mtimeMs, PartPath(id), InfoPath(id), _clock.GetUtcNow());
        await using (new FileStream(session.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }
        WriteInfo(session);
        _sessions[session.Id] = session;
        _log.LogInformation("Upload {Id} started for {Path} ({Size} bytes, in pieces)", session.Id, path, size);
        return new(StartStatus.Started, session.Id);
    }

    /// <summary>How much of the upload the server has, or null when there is no such session.</summary>
    public long? Received(string id) => _sessions.TryGetValue(id, out var s) && !s.Closed ? s.Received : null;

    /// <summary>
    /// Where the next piece starts, and whether the session is busy (a piece is still arriving), so the client
    /// waits instead of sending a piece that would be refused. Null when there is no such session.
    /// </summary>
    public UploadStatus? Status(string id) =>
        _sessions.TryGetValue(id, out var s) && !s.Closed ? new UploadStatus(id, s.Received, Busy: s.Gate.CurrentCount == 0) : null;

    /// <summary>
    /// Appends a piece that starts at <paramref name="offset"/>, which must equal what the server
    /// already has. When the connection breaks part-way, the bytes that did arrive are kept.
    /// </summary>
    public async Task<ChunkResult> AppendAsync(string id, long offset, Stream body, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new(ChunkStatus.NotFound);
        if (!await s.Gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new(ChunkStatus.Busy, s.Received); // another piece of this upload is still arriving
        try
        {
            if (s.Closed)
                return new(ChunkStatus.NotFound);
            if (offset != s.Received)
                return new(ChunkStatus.WrongOffset, s.Received);
            s.LastActivity = _clock.GetUtcNow();

            await using var fs = new FileStream(s.PartPath, FileMode.Open, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            fs.SetLength(s.Received); // drop anything a failed write left behind
            fs.Position = s.Received;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buffer = new byte[1024 * 1024];
            try
            {
                while (true)
                {
                    stall.CancelAfter(_store.UploadStallTimeout);
                    var n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    if (s.Received + n > s.Size)
                    {
                        Discard(s, "it sent more than its declared size");
                        return new(ChunkStatus.TooMuchData);
                    }
                    await fs.WriteAsync(buffer.AsMemory(0, n), CancellationToken.None).ConfigureAwait(false);
                    s.Hash.AppendData(buffer, 0, n);
                    s.Received += n;
                    s.LastActivity = _clock.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogInformation("Upload {Id} of {Path} stalled for {Timeout}; it can continue from {Received} bytes", s.Id, s.Path, _store.UploadStallTimeout, s.Received);
                fs.Flush(flushToDisk: true);
                return new(ChunkStatus.Stalled, s.Received);
            }
            // On disk after every piece: the part outlives a restart, and the commit never waits for gigabytes to be written.
            fs.Flush(flushToDisk: true);
            return new(ChunkStatus.Ok, s.Received);
        }
        finally
        {
            if (s.Closed)
                Cleanup(s);
            s.Gate.Release();
        }
    }

    /// <summary>
    /// Finishes the upload: every byte must have arrived and match <paramref name="hash"/>. Then the
    /// file is committed exactly like a single PUT (base check, history, manifest).
    /// </summary>
    public async Task<CommitResult> CommitAsync(string id, string hash, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new(CommitStatus.NotFound);
        if (!await s.Gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new(CommitStatus.Busy, Message: "A piece of this upload is still arriving.");
        try
        {
            if (s.Closed)
                return new(CommitStatus.NotFound);
            if (s.Received != s.Size)
                return new(CommitStatus.Incomplete, Message: $"The server has {s.Received} of {s.Size} bytes.");
            var actual = ContentHash.ToHex(s.Hash.GetCurrentHash());
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                Discard(s, "its content does not match the hash the client sent");
                return new(CommitStatus.HashMismatch, Message: "The uploaded content does not match its hash (the file changed while it was sent?).");
            }
            Close(s);
            await using (var fs = new FileStream(s.PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.Flush(flushToDisk: true);
            var modified = s.ModifiedMs ?? _clock.GetUtcNow().ToUnixTimeMilliseconds();
            var change = await _store.CommitUploadAsync(s.Path, s.ExpectedBase, s.PartPath, actual, s.Size, modified).ConfigureAwait(false);
            return new(CommitStatus.Done, change);
        }
        finally
        {
            if (s.Closed)
                Cleanup(s);
            s.Gate.Release();
        }
    }

    /// <summary>Drops a session and its part file. Unknown ids are fine (abort is idempotent).</summary>
    public void Abort(string id)
    {
        if (_sessions.TryGetValue(id, out var s))
            Discard(s, "the client cancelled it");
    }

    /// <summary>Drops sessions that received nothing for <see cref="IdleTimeout"/>.</summary>
    public int ExpireIdle()
    {
        var cutoff = _clock.GetUtcNow() - IdleTimeout;
        var expired = 0;
        foreach (var s in _sessions.Values)
        {
            if (s.LastActivity < cutoff)
            {
                Discard(s, "it was idle for too long");
                expired++;
            }
        }
        return expired;
    }

    private void Discard(Session s, string reason)
    {
        if (!Close(s))
            return;
        _log.LogInformation("Upload {Id} of {Path} dropped: {Reason}", s.Id, s.Path, reason);
        // The holder of the gate deletes the part file when it finishes (see the finally blocks).
        if (s.Gate.Wait(0))
        {
            Cleanup(s);
            s.Gate.Release();
        }
    }

    /// <summary>Marks the session closed and forgets it. False when it was already closed.</summary>
    private bool Close(Session s)
    {
        lock (s)
        {
            if (s.Closed)
                return false;
            s.Closed = true;
        }
        _sessions.TryRemove(s.Id, out _);
        return true;
    }

    /// <summary>Deletes the part file and description of a closed session. Only the holder of its gate calls this.</summary>
    private static void Cleanup(Session s)
    {
        TryDelete(s.InfoPath);
        TryDelete(s.PartPath);
        s.Hash.Dispose();
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private string PartPath(string id) => Path.Combine(_store.Paths.Tmp, id + PartSuffix);

    private string InfoPath(string id) => Path.Combine(_store.Paths.Tmp, id + InfoSuffix);

    /// <summary>What a session is for, written next to its part file so it can be taken up after a restart.</summary>
    private sealed record SessionInfo(string Id, string Path, string? Base, long Size, long? ModifiedMs);

    /// <summary>Writes the description (on disk before the session is used; replaced atomically).</summary>
    private static void WriteInfo(Session s)
    {
        var temp = s.InfoPath + ".new";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, new SessionInfo(s.Id, s.Path, s.ExpectedBase, s.Size, s.ModifiedMs));
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, s.InfoPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            TryDelete(s.PartPath);
            throw;
        }
    }

    /// <summary>
    /// Takes up the uploads kept over a restart (<see cref="SyncStore"/> left their part files and descriptions in
    /// tmp/ when they had data within the last hour). Each is busy until its part file is hashed again in the
    /// background; anything unreadable or inconsistent is dropped (the client then sends that file again).
    /// </summary>
    private void RestoreKept()
    {
        if (!Directory.Exists(_store.Paths.Tmp))
            return;
        foreach (var infoFile in Directory.EnumerateFiles(_store.Paths.Tmp, "*" + InfoSuffix))
        {
            if (!TryParseFileName(Path.GetFileName(infoFile), out var id) || !infoFile.EndsWith(InfoSuffix, StringComparison.Ordinal))
                continue;
            var partFile = PartPath(id);
            SessionInfo? info = null;
            long length = -1;
            try
            {
                info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllBytes(infoFile));
                if (File.Exists(partFile))
                    length = new FileInfo(partFile).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.LogWarning("Upload {Id} kept over the restart cannot be read: {Error}", id, ex.Message);
            }
            if (info is null || info.Id != id || !PathRules.IsValid(info.Path) || info.Size < 0 || length < 0 || length > info.Size
                || (info.Base is not null && !ContentHash.IsValid(info.Base)) || _sessions.Count >= MaxSessions)
            {
                TryDelete(infoFile);
                TryDelete(partFile);
                continue;
            }

            var s = new Session(id, info.Path, info.Base, info.Size, info.ModifiedMs, partFile, infoFile, _clock.GetUtcNow()) { Received = length };
            s.Gate.Wait(0); // busy until what arrived is hashed again
            _sessions[id] = s;
            _log.LogInformation("Upload {Id} of {Path} was kept over the restart ({Received} of {Size} bytes); checking what arrived", id, info.Path, length, info.Size);
            _ = Task.Run(() => RehashAsync(s));
        }
    }

    /// <summary>Hashes the part file of a session kept over a restart (the running hash lived only in memory), then lets it go on.</summary>
    private async Task RehashAsync(Session s)
    {
        var stopped = false;
        try
        {
            await using var fs = new FileStream(s.PartPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            while (total < s.Received)
            {
                var n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, s.Received - total)), _stopping.Token).ConfigureAwait(false);
                if (n == 0)
                    throw new IOException("The part file got shorter.");
                s.Hash.AppendData(buffer, 0, n);
                total += n;
            }
            s.LastActivity = _clock.GetUtcNow();
            _log.LogInformation("Upload {Id} of {Path} can continue from {Received} bytes", s.Id, s.Path, s.Received);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            stopped = true; // shutting down: the files stay for the next start
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Discard(s, "its part file could not be read after the restart: " + ex.Message);
        }
        finally
        {
            if (!stopped)
            {
                if (s.Closed)
                    Cleanup(s);
                s.Gate.Release();
            }
        }
    }

    /// <summary>Stops checking kept part files (the server is shutting down; they are checked again at the next start).</summary>
    public void Dispose() => _stopping.Cancel();

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in tmp/, which is cleaned at the next start.
        }
    }

    private sealed class Session(string id, string path, string? expectedBase, long size, long? modifiedMs, string partPath, string infoPath, DateTimeOffset now)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public string? ExpectedBase { get; } = expectedBase;
        public long Size { get; } = size;
        public long? ModifiedMs { get; } = modifiedMs;
        public string PartPath { get; } = partPath;
        public string InfoPath { get; } = infoPath;
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Received { get; set; }
        public DateTimeOffset LastActivity { get; set; } = now;
        public volatile bool Closed;
    }
}

/// <summary>Drops abandoned upload sessions every few minutes (see <see cref="UploadSessions.IdleTimeout"/>).</summary>
public sealed class UploadSessionSweeper(UploadSessions sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                sessions.ExpireIdle();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
