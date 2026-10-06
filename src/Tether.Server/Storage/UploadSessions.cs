using System.Collections.Concurrent;
using System.Security.Cryptography;
using Tether.Core.Hashing;
using Tether.Core.Paths;

namespace Tether.Server.Storage;

/// <summary>
/// Uploads that arrive in pieces (POST /api/upload, PUT /api/upload/{id}, POST /api/upload/{id}/commit).
/// A file of any size then fits through a proxy that caps one request (Cloudflare: 100 MB), and an
/// upload cut off half-way continues where it stopped instead of starting again. Sessions live in
/// memory with their part file in tmp/: a restart empties tmp/ and the client sends that file again.
/// Every byte the server takes in is written, hashed and counted together, so "received" is always
/// exactly what the part file holds and the client can resume from it.
/// </summary>
public sealed class UploadSessions
{
    /// <summary>Largest piece the server takes in one request (the client sends 50 MiB).</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;

    public const int MaxSessions = 64;

    /// <summary>A session that receives nothing for this long is dropped with its part file.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    private readonly SyncStore _store;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public UploadSessions(SyncStore store, ILogger<UploadSessions> log, TimeProvider? clock = null)
    {
        _store = store;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public int Count => _sessions.Count;

    public enum StartStatus { Started, Rejected, TooMany }

    public enum ChunkStatus { Ok, NotFound, WrongOffset, TooMuchData, Stalled }

    public enum CommitStatus { Done, NotFound, Incomplete, HashMismatch }

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

        var session = new Session(NewId(), path, expectedBase, size, mtimeMs, _store.NewUploadPartPath(), _clock.GetUtcNow());
        await using (new FileStream(session.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }
        _sessions[session.Id] = session;
        _log.LogInformation("Upload {Id} started for {Path} ({Size} bytes, in pieces)", session.Id, path, size);
        return new(StartStatus.Started, session.Id);
    }

    /// <summary>How much of the upload the server has, or null when there is no such session.</summary>
    public long? Received(string id) => _sessions.TryGetValue(id, out var s) && !s.Closed ? s.Received : null;

    /// <summary>
    /// Appends a piece that starts at <paramref name="offset"/>, which must equal what the server
    /// already has. When the connection breaks part-way, the bytes that did arrive are kept.
    /// </summary>
    public async Task<ChunkResult> AppendAsync(string id, long offset, Stream body, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var s))
            return new(ChunkStatus.NotFound);
        if (!await s.Gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new(ChunkStatus.WrongOffset, s.Received); // another piece of this upload is still arriving
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
                return new(ChunkStatus.Stalled, s.Received);
            }
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
            return new(CommitStatus.Incomplete, Message: "A piece of this upload is still arriving.");
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

    /// <summary>Deletes the part file of a closed session. Only the holder of its gate calls this.</summary>
    private static void Cleanup(Session s)
    {
        TryDelete(s.PartPath);
        s.Hash.Dispose();
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in tmp/, which is emptied at the next start.
        }
    }

    private sealed class Session(string id, string path, string? expectedBase, long size, long? modifiedMs, string partPath, DateTimeOffset now)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public string? ExpectedBase { get; } = expectedBase;
        public long Size { get; } = size;
        public long? ModifiedMs { get; } = modifiedMs;
        public string PartPath { get; } = partPath;
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
