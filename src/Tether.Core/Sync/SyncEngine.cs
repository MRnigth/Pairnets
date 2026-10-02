using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tether.Core.Api;
using Tether.Core.Hashing;
using Tether.Core.Paths;
using Tether.Core.State;

namespace Tether.Core.Sync;

/// <summary>
/// Runs sync passes for one folder. A pass recomputes everything from disk, the state database and
/// the server manifest, so an interrupted pass is always repaired by the next one.
/// Not thread-safe: the <see cref="SyncRunner"/> guarantees that only one pass runs at a time.
/// </summary>
public sealed class SyncEngine
{
    private readonly EngineOptions _options;
    private readonly ITetherApi _api;
    private readonly StateDb _state;
    private readonly ILocalTrash _trash;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly EngineHooks _hooks;
    private readonly LocalScanner _scanner;
    private readonly ConcurrentDictionary<string, long> _recentlyTouched = new(StringComparer.Ordinal);

    public SyncEngine(EngineOptions options, ITetherApi api, StateDb state, ILocalTrash? trash = null,
        ILogger? log = null, TimeProvider? clock = null, EngineHooks? hooks = null)
    {
        _options = options;
        _api = api;
        _state = state;
        _trash = trash ?? new PermanentDeleteTrash();
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _clock = clock ?? TimeProvider.System;
        _hooks = hooks ?? new EngineHooks();
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Folder));
        Ignore = new IgnoreList(options.ExtraIgnore);
        _scanner = new LocalScanner(Root, Ignore, state, _clock, _log)
        {
            StabilityWindow = options.StabilityWindow,
            RacyWindow = options.RacyWindow,
        };
    }

    public string Root { get; }

    public IgnoreList Ignore { get; }

    public string DeviceName => _options.DeviceName;

    public StateDb State => _state;

    public string MarkerPath => Path.Combine(Root, PathRules.MarkerFileName);

    public string TempDirectory => Path.Combine(Root, PathRules.TempFolderName);

    public event Action<SyncProgress>? Progress;

    public event Action<ConflictInfo>? ConflictCreated;

    public event Action<PathWarningInfo>? PathWarningRaised;

    /// <summary>Raised after a file was uploaded, downloaded or deleted (for activity lists). May be raised concurrently.</summary>
    public event Action<SyncAction, string>? FileSynced;

    /// <summary>Raised once per pass after planning: uploads (incl. conflicts), downloads, and the bytes they will move.</summary>
    public event Action<int, int, long>? ExecutionStarting;

    /// <summary>Raised when an upload or download (finished, failed or skipped) no longer runs.</summary>
    public event Action<string>? TransferFinished;

    /// <summary>True if the engine itself wrote/deleted this path recently (used to ignore our own watcher events).</summary>
    public bool WasRecentlyTouched(string path, TimeSpan window)
    {
        if (!_recentlyTouched.TryGetValue(path, out var ticks))
            return false;
        return _clock.GetUtcNow().UtcTicks - ticks < window.Ticks;
    }

    /// <summary>"Allow these deletions": arms the deletions of the last blocked pass for exactly one pass.</summary>
    public int ApproveBlockedDeletions()
    {
        var count = _state.ApprovePendingDeletes();
        _log.LogWarning("User approved {Count} blocked deletion(s) for the next pass", count);
        return count;
    }

    /// <summary>Pending deletions of the last blocked pass.</summary>
    public IReadOnlyList<(DeleteSide Side, string Path)> PendingDeletes => _state.GetPendingDeletes();

    /// <summary>Accepts the marker already present in the folder (folder moved, or state was reset).</summary>
    public void AdoptExistingMarker()
    {
        var marker = ReadMarkerFile() ?? throw new InvalidOperationException("The folder has no readable .tether-marker.");
        _state.MarkerId = marker;
        _log.LogWarning("Adopted existing marker {Marker} for {Folder}", marker, Root);
    }

    /// <summary>
    /// Re-links this folder to the current server after it was replaced or restored from a backup.
    /// All bases are forgotten, so the next pass merges: nothing is deleted or overwritten,
    /// differing files become conflict copies.
    /// </summary>
    public void RelinkToServer()
    {
        _state.ClearAllBases();
        _log.LogWarning("Re-linked {Folder} to the server; the next pass merges without deleting", Root);
    }

    public string? ReadMarkerFile()
    {
        try
        {
            if (!File.Exists(MarkerPath))
                return null;
            var text = File.ReadAllText(MarkerPath).Trim();
            return Guid.TryParse(text, out var g) ? g.ToString("D") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Cannot read marker file: {Error}", ex.Message);
            return null;
        }
    }

    public async Task<PassResult> RunPassAsync(PassOptions options, CancellationToken ct)
    {
        var result = new PassResult();
        try
        {
            await RunPassCoreAsync(options, result, ct).ConfigureAwait(false);
        }
        catch (TetherNetworkException ex)
        {
            result.Outcome = PassOutcome.Offline;
            result.Message = ex.Message;
            _log.LogWarning("Pass aborted, server unreachable: {Error}", ex.Message);
        }
        catch (TetherAuthException ex)
        {
            result.Outcome = PassOutcome.AuthFailed;
            result.Message = ex.Message;
            _log.LogError("Pass aborted: {Error}", ex.Message);
        }
        catch (ScanFailedException ex)
        {
            if (!Directory.Exists(Root))
            {
                result.Outcome = PassOutcome.Blocked;
                result.BlockReason = BlockReason.FolderMissing;
                result.Message = $"The sync folder {Root} is missing.";
            }
            else
            {
                result.Outcome = PassOutcome.Failed;
                result.Message = ex.Message;
            }
            _log.LogWarning("Pass aborted, scan failed: {Error}", ex.Message);
        }
        catch (TetherProtocolException ex)
        {
            result.Outcome = PassOutcome.Failed;
            result.Message = ex.Message;
            _log.LogError("Pass aborted, unexpected server answer: {Error}", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            result.Outcome = PassOutcome.Failed;
            result.Message = ex.Message;
            _log.LogError(ex, "Pass aborted by a local error");
        }

        _log.LogInformation("Pass ({Reason}) finished: {Result}", options.Reason, result);
        return result;
    }

    private PassResult Block(PassResult result, BlockReason reason, string message)
    {
        result.Outcome = PassOutcome.Blocked;
        result.BlockReason = reason;
        result.Message = message;
        _log.LogWarning("Pass blocked ({Reason}): {Message}", reason, message);
        return result;
    }

    private async Task RunPassCoreAsync(PassOptions options, PassResult result, CancellationToken ct)
    {
        // ---- 1. Folder and marker: never mistake a missing or wrong folder for "everything was deleted".
        if (!Directory.Exists(Root))
        {
            Block(result, BlockReason.FolderMissing, $"The sync folder {Root} is missing. Is the drive connected?");
            return;
        }

        var tracked = _state.CountTracked();
        var stateMarker = _state.MarkerId;
        var fileMarker = ReadMarkerFile();
        if (stateMarker is null)
        {
            if (fileMarker is not null)
            {
                Block(result, BlockReason.ForeignMarker,
                    "This folder was already synced by Tether with a different state. Confirm before syncing it again.");
                return;
            }
            stateMarker = CreateMarker();
        }
        else if (fileMarker is null)
        {
            Block(result, BlockReason.MarkerMissing,
                $"The folder {Root} has no .tether-marker although it was synced before. Wrong folder or drive?");
            return;
        }
        else if (!string.Equals(fileMarker, stateMarker, StringComparison.OrdinalIgnoreCase))
        {
            Block(result, BlockReason.MarkerMismatch, $"The marker in {Root} belongs to a different sync state.");
            return;
        }

        CleanTempDirectory();

        // ---- 2. Server manifest (full or delta) into the local mirror.
        var cursor = _state.Cursor;
        var knownServer = _state.ServerId;
        var full = options.FullManifest || cursor == 0 || knownServer is null;
        var manifest = await _api.GetManifestAsync(full ? null : cursor, ct).ConfigureAwait(false);
        if (knownServer is not null && !string.Equals(manifest.ServerId, knownServer, StringComparison.Ordinal))
        {
            Block(result, BlockReason.ServerChanged,
                "The server is not the one this folder was synced with. Re-link to merge safely.");
            return;
        }
        if (manifest.Version < cursor)
        {
            Block(result, BlockReason.ServerRolledBack,
                $"The server went back in time (version {manifest.Version} < {cursor}); it was probably restored from a backup. Re-link to merge safely.");
            return;
        }
        _state.ApplyRemote(manifest.Entries, replaceAll: full, manifest.Version, manifest.ServerId);
        cursor = manifest.Version;

        // ---- 3. Local scan.
        var scan = await _scanner.ScanAsync(ct).ConfigureAwait(false);
        result.Unstable = scan.UnstableCount;
        var warnings = _state.LoadWarnings();
        RecordInvalidLocalNames(scan, warnings, cursor, result);

        // ---- 4. Plan with the three-hash decision.
        var files = _state.LoadFiles();
        var remote = _state.LoadRemote();
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.UnionWith(scan.Files.Keys);
        paths.UnionWith(remote.Keys);
        foreach (var f in files.Values)
        {
            if (f.BaseHash is not null)
                paths.Add(f.Path);
        }

        var plan = new List<PlannedAction>();
        foreach (var path in paths)
        {
            if (Ignore.IsIgnored(path) || !PathRules.IsValid(path) || scan.IsUnderUnknownPrefix(path))
                continue;
            scan.Files.TryGetValue(path, out var local);
            if (local is { Stable: false })
                continue;
            remote.TryGetValue(path, out var remoteEntry);
            files.TryGetValue(path, out var fileState);
            var l = local?.Hash;
            var s = remoteEntry is { Deleted: false } ? remoteEntry.Hash : null;
            var b = fileState?.BaseHash;
            var action = SyncDecision.Decide(l, s, b);
            if (action == SyncAction.None)
                continue;
            if (action is SyncAction.Upload or SyncAction.Conflict
                && warnings.TryGetValue(path, out var w)
                && w.LocalHash == l && w.ServerCursor == cursor)
            {
                result.Warnings++;
                continue; // Persistent warning: retried only when the file or the server changes.
            }
            plan.Add(new PlannedAction(path, action, l, s, b));
        }
        ClearResolvedWarnings(warnings, scan);

        // ---- 5. Mass-delete guard (before anything is executed).
        var wanted = plan
            .Where(p => p.Action is SyncAction.DeleteLocal or SyncAction.DeleteRemote)
            .Select(p => (Side: p.Action == SyncAction.DeleteLocal ? DeleteSide.Local : DeleteSide.Remote, p.Path))
            .ToHashSet();
        var deleteLocal = wanted.Count(w => w.Side == DeleteSide.Local);
        var deleteRemote = wanted.Count - deleteLocal;
        var limit = Math.Min(_options.MassDeleteFraction * tracked, _options.MassDeleteMax);
        var folderEmpty = tracked > 0 && scan.FileCount == 0;
        var approval = _state.ConsumeApproval();
        if (deleteLocal > limit || deleteRemote > limit || (folderEmpty && wanted.Count > 0))
        {
            if (approval is null || !wanted.IsSubsetOf(approval))
            {
                _state.SetPendingDeletes(wanted);
                result.BlockedDeletes.AddRange(wanted.OrderBy(w => w.Path, StringComparer.Ordinal));
                var what = $"{deleteLocal} local and {deleteRemote} server deletion(s) out of {tracked} tracked file(s)";
                Block(result, folderEmpty ? BlockReason.FolderEmpty : BlockReason.MassDelete,
                    folderEmpty
                        ? $"The folder is empty but {tracked} file(s) were synced before; {what} were blocked."
                        : $"This pass would make {what}; nothing was deleted.");
                return;
            }
            _log.LogWarning("Executing {Count} deletion(s) approved by the user", wanted.Count);
        }
        _state.ClearPendingDeletes();

        // ---- 6. Execute. Deletions first so file/folder replacements never collide.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in scan.Files.Keys)
            taken.Add(PathRules.CaseKey(p));
        foreach (var e in remote.Values)
        {
            if (!e.Deleted)
                taken.Add(PathRules.CaseKey(e.Path));
        }

        if (options.DeferDownloads)
        {
            var deferred = plan.RemoveAll(p => p.Action == SyncAction.Download);
            result.Deferred += deferred;
            if (deferred > 0)
                _log.LogInformation("{Count} download(s) deferred until the other computer finishes its batch", deferred);
        }

        var ordered = plan.OrderBy(p => Order(p.Action)).ThenBy(p => p.Path, StringComparer.Ordinal).ToList();
        var ctx = new PassContext(result, cursor, taken, ordered.Count);
        long plannedBytes = 0;
        foreach (var item in ordered)
        {
            if (item.Action is SyncAction.Upload or SyncAction.Conflict && scan.Files.TryGetValue(item.Path, out var lf))
                plannedBytes += lf.Size;
            if (item.Action is SyncAction.Download or SyncAction.Conflict && remote.TryGetValue(item.Path, out var re))
            {
                plannedBytes += re.Size;
                ctx.Sizes[item.Path] = re.Size;
            }
        }
        ExecutionStarting?.Invoke(
            ordered.Count(p => p.Action is SyncAction.Upload or SyncAction.Conflict),
            ordered.Count(p => p.Action == SyncAction.Download),
            plannedBytes);

        // Deletes and conflicts one by one (they rename and prune folders), then uploads and
        // downloads several at a time: each small file costs a round trip, so overlap them.
        foreach (var phase in ordered.GroupBy(p => Order(p.Action)).OrderBy(g => g.Key))
        {
            var parallel = phase.Key is 3 or 4 ? Math.Max(1, _options.MaxParallelTransfers) : 1;
            await RunLimitedAsync(phase.ToList(), parallel, item => ExecuteIsolatedAsync(item, ctx, ct), ct).ConfigureAwait(false);
        }
        Progress?.Invoke(new SyncProgress(null, null, 0, 0, ctx.FilesDone, ctx.FilesTotal));
    }

    /// <summary>Runs one planned action; a problem with one file never aborts the pass.</summary>
    private async Task ExecuteIsolatedAsync(PlannedAction item, PassContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (_hooks.BeforeTransfer is { } before && item.Action is SyncAction.Upload or SyncAction.Download)
                await before(item.Action, item.Path).ConfigureAwait(false);
            await ExecuteAsync(item, ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TetherProtocolException)
        {
            lock (ctx.Gate)
            {
                ctx.Result.Errors++;
                ctx.Result.ErrorMessages.Add($"{item.Path}: {ex.Message}");
            }
            _log.LogWarning("{Action} failed for {Path}: {Error}", item.Action, item.Path, ex.Message);
        }
        finally
        {
            if (item.Action is SyncAction.Upload or SyncAction.Download or SyncAction.Conflict)
                TransferFinished?.Invoke(item.Path);
        }
        lock (ctx.Gate)
            ctx.FilesDone++;
        if (_hooks.AfterAction is { } hook)
            await hook(item.Action, item.Path).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="body"/> for every item, at most <paramref name="limit"/> at a time. The
    /// first exception that escapes a body (server unreachable, bad token) stops new work, lets the
    /// running ones finish or cancel, and is rethrown itself, so the pass ends exactly as it would
    /// one-by-one.
    /// </summary>
    internal static async Task RunLimitedAsync<T>(IReadOnlyList<T> items, int limit, Func<T, Task> body, CancellationToken ct)
    {
        if (limit <= 1 || items.Count <= 1)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await body(item).ConfigureAwait(false);
            }
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(limit, limit);
        Exception? first = null;
        var running = new List<Task>(items.Count);
        foreach (var item in items)
        {
            try
            {
                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            running.Add(Task.Run(async () =>
            {
                try
                {
                    await body(item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref first, ex, null);
                    await stop.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            }, CancellationToken.None));
        }
        await Task.WhenAll(running).ConfigureAwait(false);
        if (first is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        ct.ThrowIfCancellationRequested();
    }

    private static int Order(SyncAction action) => action switch
    {
        SyncAction.DeleteRemote => 0,
        SyncAction.DeleteLocal => 1,
        SyncAction.Conflict => 2,
        SyncAction.Upload => 3,
        SyncAction.Download => 4,
        _ => 5,
    };

    private Task ExecuteAsync(PlannedAction item, PassContext ctx, CancellationToken ct)
    {
        switch (item.Action)
        {
            case SyncAction.RecordBase:
                _state.SetBase(item.Path, item.Local);
                lock (ctx.Gate)
                    ctx.Result.Recorded++;
                return Task.CompletedTask;
            case SyncAction.ClearBase:
                _state.RemoveFile(item.Path);
                return Task.CompletedTask;
            case SyncAction.Upload:
                return UploadAsync(item.Path, item.Server ?? ContentHash.NoneBase, ctx, ct);
            case SyncAction.Download:
                return DownloadAsync(item.Path, item.Local, ctx, ct);
            case SyncAction.Conflict:
                return ConflictAsync(item, ctx, ct);
            case SyncAction.DeleteLocal:
                return DeleteLocalAsync(item, ctx, ct);
            case SyncAction.DeleteRemote:
                return DeleteRemoteAsync(item, ctx, ct);
            default:
                return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ upload

    private async Task UploadAsync(string path, string baseHash, PassContext ctx, CancellationToken ct)
    {
        var full = ResolveLocal(path);
        FileStream stream;
        try
        {
            stream = ContentHash.OpenForSharedRead(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked, or gone since the scan: unknown this pass, try again soon.
            lock (ctx.Gate)
                ctx.Result.Unstable++;
            _log.LogInformation("Skipping upload of {Path} this pass: {Error}", path, ex.Message);
            return;
        }

        await using (stream)
        {
            var info = new FileInfo(full);
            var mtimeTicks = info.LastWriteTimeUtc.Ticks;
            var mtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            var total = stream.Length;
            Report(path, "upload", 0, total, ctx);
            var (res, sentHash, sentBytes) = await _api.UploadAsync(path, baseHash, mtimeMs, stream,
                bytes => Report(path, "upload", bytes, total, ctx), ct).ConfigureAwait(false);
            switch (res.Outcome)
            {
                case ApiOutcome.Ok:
                    var entry = res.Entry!;
                    if (!string.Equals(entry.Hash, sentHash, StringComparison.Ordinal))
                        throw new TetherProtocolException($"Server stored different content for {path}.");
                    info.Refresh();
                    var unchanged = info.Exists && info.Length == sentBytes && info.LastWriteTimeUtc.Ticks == mtimeTicks;
                    _state.SetSynced(path, entry.Hash!, unchanged ? sentBytes : -1, unchanged ? mtimeTicks : 0,
                        unchanged ? sentHash : null, _clock.GetUtcNow().UtcTicks);
                    _state.UpsertRemote(entry);
                    _state.ClearWarning(path);
                    lock (ctx.Gate)
                        ctx.Result.Uploaded++;
                    FileSynced?.Invoke(SyncAction.Upload, path);
                    _log.LogInformation("Uploaded {Path} ({Bytes} bytes)", path, sentBytes);
                    break;
                case ApiOutcome.Conflict:
                    lock (ctx.Gate)
                        ctx.Result.Deferred++;
                    _log.LogInformation("Upload of {Path} deferred: the server changed meanwhile", path);
                    break;
                case ApiOutcome.CaseCollision:
                case ApiOutcome.InvalidName:
                    var code = res.Outcome == ApiOutcome.CaseCollision ? ErrorCodes.CaseCollision : ErrorCodes.InvalidName;
                    RaiseWarning(path, code, res.Message, sentHash.Length > 0 ? sentHash : await TryHashAsync(full, ct).ConfigureAwait(false), ctx);
                    break;
                default:
                    throw new TetherProtocolException($"Unexpected upload result {res.Outcome} for {path}.");
            }
        }
    }

    // ------------------------------------------------------------------ download

    private async Task DownloadAsync(string path, string? expectedLocalHash, PassContext ctx, CancellationToken ct)
    {
        var full = ResolveLocal(path);
        var tmp = Path.Combine(EnsureTempDirectory(), Guid.NewGuid().ToString("N") + ".part");
        try
        {
            DownloadResult dl;
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ContentHash.BufferSize, FileOptions.Asynchronous))
            {
                var target = _hooks.WrapDownloadStream?.Invoke(fs) ?? fs;
                Report(path, "download", 0, 0, ctx);
                dl = await _api.DownloadAsync(path, target, bytes => Report(path, "download", bytes, 0, ctx), ct).ConfigureAwait(false);
                await target.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }

            if (!dl.Found)
            {
                lock (ctx.Gate)
                    ctx.Result.Deferred++;
                _log.LogInformation("Download of {Path} deferred: it disappeared from the server", path);
                return;
            }
            if (dl.ModifiedMs > 0)
                File.SetLastWriteTimeUtc(tmp, DateTimeOffset.FromUnixTimeMilliseconds(dl.ModifiedMs).UtcDateTime);

            EnsureParentDirectory(path, full);
            await PreserveIfChangedAsync(path, full, expectedLocalHash, ctx, ct).ConfigureAwait(false);
            File.Move(tmp, full, overwrite: true);
            MarkTouched(path);

            var info = new FileInfo(full);
            _state.SetSynced(path, dl.Hash, info.Length, info.LastWriteTimeUtc.Ticks, dl.Hash, _clock.GetUtcNow().UtcTicks);
            lock (ctx.Gate)
                ctx.Result.Downloaded++;
            FileSynced?.Invoke(SyncAction.Download, path);
            _log.LogInformation("Downloaded {Path} ({Bytes} bytes)", path, dl.Size);
        }
        finally
        {
            TryDeleteFile(tmp);
        }
    }

    /// <summary>
    /// Safety guard (d): before the engine overwrites a local file, verify it still has the content
    /// the decision was based on. If it changed in the meantime, keep it as a conflict copy.
    /// </summary>
    private async Task PreserveIfChangedAsync(string path, string full, string? expectedHash, PassContext ctx, CancellationToken ct)
    {
        if (Directory.Exists(full))
            throw new IOException($"A folder named like the file '{path}' exists locally.");
        if (!File.Exists(full))
            return;
        var (current, _) = await ContentHash.OfFileAsync(full, ct).ConfigureAwait(false);
        if (string.Equals(current, expectedHash, StringComparison.Ordinal))
            return;

        var copy = MakeConflictName(path, ctx);
        File.Move(full, ResolveLocal(copy), overwrite: false);
        MarkTouched(copy);
        _log.LogWarning("{Path} changed during the pass; kept the local version as {Copy}", path, copy);
        RaiseConflict(path, copy, ctx);
    }

    // ------------------------------------------------------------------ conflict

    private async Task ConflictAsync(PlannedAction item, PassContext ctx, CancellationToken ct)
    {
        var full = ResolveLocal(item.Path);
        var copy = MakeConflictName(item.Path, ctx);
        var copyFull = ResolveLocal(copy);
        // 1. Keep the local version under a new name (never overwritten, never lost).
        File.Move(full, copyFull, overwrite: false);
        MarkTouched(item.Path);
        MarkTouched(copy);
        _log.LogWarning("Conflict on {Path}: both sides changed; local version saved as {Copy}", item.Path, copy);
        RaiseConflict(item.Path, copy, ctx);

        // 2. Upload the copy as a new file so it reaches the other PC too.
        await UploadAsync(copy, ContentHash.NoneBase, ctx, ct).ConfigureAwait(false);

        // 3. Bring the server version to the original name.
        await DownloadAsync(item.Path, expectedLocalHash: null, ctx, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ deletes

    private async Task DeleteLocalAsync(PlannedAction item, PassContext ctx, CancellationToken ct)
    {
        var full = ResolveLocal(item.Path);
        if (!File.Exists(full))
        {
            _state.RemoveFile(item.Path);
            return;
        }

        // Safety guard (d): only delete exactly the content that was agreed and deleted elsewhere.
        var (current, _) = await ContentHash.OfFileAsync(full, ct).ConfigureAwait(false);
        if (!string.Equals(current, item.Local, StringComparison.Ordinal))
        {
            lock (ctx.Gate)
                ctx.Result.Deferred++;
            _log.LogWarning("{Path} changed during the pass; not deleting it (it will be uploaded instead)", item.Path);
            return;
        }

        _trash.Delete(full);
        MarkTouched(item.Path);
        _state.RemoveFile(item.Path);
        PruneEmptyParents(item.Path);
        lock (ctx.Gate)
            ctx.Result.DeletedLocal++;
        FileSynced?.Invoke(SyncAction.DeleteLocal, item.Path);
        _log.LogInformation("Deleted local {Path} (deleted on another device)", item.Path);
    }

    private async Task DeleteRemoteAsync(PlannedAction item, PassContext ctx, CancellationToken ct)
    {
        Report(item.Path, "delete", 0, 0, ctx);
        var res = await _api.DeleteAsync(item.Path, item.Server!, ct).ConfigureAwait(false);
        switch (res.Outcome)
        {
            case ApiOutcome.Ok:
            case ApiOutcome.NotFound:
                _state.RemoveFile(item.Path);
                if (res.Entry is not null)
                    _state.UpsertRemote(res.Entry);
                lock (ctx.Gate)
                    ctx.Result.DeletedRemote++;
                FileSynced?.Invoke(SyncAction.DeleteRemote, item.Path);
                _log.LogInformation("Deleted {Path} on the server (deleted here)", item.Path);
                break;
            case ApiOutcome.Conflict:
                lock (ctx.Gate)
                    ctx.Result.Deferred++;
                _log.LogInformation("Delete of {Path} deferred: the server changed meanwhile", item.Path);
                break;
            default:
                throw new TetherProtocolException($"Unexpected delete result {res.Outcome} for {item.Path}.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private void RecordInvalidLocalNames(ScanResult scan, Dictionary<string, PathWarning> warnings, long cursor, PassResult result)
    {
        foreach (var (path, problem) in scan.InvalidNames)
        {
            result.Warnings++;
            if (warnings.TryGetValue(path, out var existing) && existing.Code == ErrorCodes.InvalidName)
                continue;
            var message = $"The name cannot be synced ({problem}). Rename the file.";
            _state.SetWarning(new PathWarning(path, ErrorCodes.InvalidName, message, null, cursor, _clock.GetUtcNow().ToUnixTimeMilliseconds()));
            PathWarningRaised?.Invoke(new PathWarningInfo(path, ErrorCodes.InvalidName, message));
            _log.LogWarning("Not syncing {Path}: invalid name ({Problem})", path, problem);
        }
    }

    private void ClearResolvedWarnings(Dictionary<string, PathWarning> warnings, ScanResult scan)
    {
        if (warnings.Count == 0)
            return;
        var invalid = scan.InvalidNames.Select(i => i.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var w in warnings.Values)
        {
            var stillThere = scan.Files.ContainsKey(w.Path) || invalid.Contains(w.Path);
            if (!stillThere)
                _state.ClearWarning(w.Path);
        }
    }

    private void RaiseWarning(string path, string code, string? message, string? localHash, PassContext ctx)
    {
        lock (ctx.Gate)
            ctx.Result.Warnings++;
        var text = message ?? (code == ErrorCodes.CaseCollision
            ? "Another file or folder with the same name in different letter case exists on the server."
            : "The server rejected this file name.");
        _state.SetWarning(new PathWarning(path, code, text, localHash, ctx.Cursor, _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        PathWarningRaised?.Invoke(new PathWarningInfo(path, code, text));
        _log.LogWarning("Not syncing {Path}: {Code} {Message}", path, code, text);
    }

    private void RaiseConflict(string path, string copy, PassContext ctx)
    {
        var info = new ConflictInfo(path, copy);
        lock (ctx.Gate)
        {
            ctx.Result.Conflicts++;
            ctx.Result.ConflictCopies.Add(info);
            ctx.Taken.Add(PathRules.CaseKey(copy));
        }
        ConflictCreated?.Invoke(info);
    }

    private string MakeConflictName(string path, PassContext ctx)
    {
        lock (ctx.Gate)
        {
            var name = ConflictNames.Make(path, _options.DeviceName, _clock.GetLocalNow().DateTime, candidate =>
            {
                if (ctx.Taken.Contains(PathRules.CaseKey(candidate)))
                    return true;
                var full = ResolveLocal(candidate);
                return File.Exists(full) || Directory.Exists(full);
            });
            ctx.Taken.Add(PathRules.CaseKey(name));
            return name;
        }
    }

    private static async Task<string?> TryHashAsync(string full, CancellationToken ct)
    {
        try
        {
            return (await ContentHash.OfFileAsync(full, ct).ConfigureAwait(false)).Hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string ResolveLocal(string path)
    {
        var full = PathRules.ResolveUnder(Root, path) ?? throw new IOException($"Refusing unsafe path '{path}'.");
        if (PathRules.HasReparsePoint(Root, path))
            throw new IOException($"Refusing '{path}': it goes through a symlink or junction.");
        return full;
    }

    private void EnsureParentDirectory(string path, string full)
    {
        var parent = PathRules.Parent(path);
        if (parent is null)
            return;
        var dir = Path.GetDirectoryName(full)!;
        if (File.Exists(dir))
            throw new IOException($"Cannot create folder for '{path}': a file named '{parent}' exists.");
        Directory.CreateDirectory(dir);
    }

    private void PruneEmptyParents(string path)
    {
        for (var parent = PathRules.Parent(path); parent is not null; parent = PathRules.Parent(parent))
        {
            var dir = PathRules.ResolveUnder(Root, parent);
            if (dir is null || !Directory.Exists(dir))
                break;
            try
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).Any())
                    break;
                info.Delete(recursive: false);
                MarkTouched(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }
        }
    }

    private string CreateMarker()
    {
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(MarkerPath, id + Environment.NewLine);
        TrySetHidden(MarkerPath);
        _state.MarkerId = id;
        _log.LogInformation("Created marker {Marker} in {Folder}", id, Root);
        return id;
    }

    private string EnsureTempDirectory()
    {
        var dir = TempDirectory;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            TrySetHidden(dir);
        }
        return dir;
    }

    private void CleanTempDirectory()
    {
        var dir = TempDirectory;
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.EnumerateFiles(dir))
            TryDeleteFile(file);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("Could not delete temp file {File}: {Error}", path, ex.Message);
        }
    }

    private static void TrySetHidden(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cosmetic only.
        }
    }

    private void MarkTouched(string path) => _recentlyTouched[path] = _clock.GetUtcNow().UtcTicks;

    private void Report(string path, string op, long bytes, long total, PassContext ctx) =>
        Progress?.Invoke(new SyncProgress(path, op, bytes,
            total > 0 ? total : ctx.Sizes.GetValueOrDefault(path), ctx.FilesDone, ctx.FilesTotal));

    private sealed record PlannedAction(string Path, SyncAction Action, string? Local, string? Server, string? Base);

    private sealed class PassContext(PassResult result, long cursor, HashSet<string> taken, int filesTotal)
    {
        /// <summary>Guards <see cref="Result"/>, <see cref="Taken"/> and <see cref="FilesDone"/>: transfers run concurrently.</summary>
        public object Gate { get; } = new();

        public PassResult Result { get; } = result;

        /// <summary>Server sizes of planned downloads (the HTTP response may not say), for progress.</summary>
        public Dictionary<string, long> Sizes { get; } = new(StringComparer.Ordinal);
        public long Cursor { get; } = cursor;
        public HashSet<string> Taken { get; } = taken;
        public int FilesTotal { get; } = filesTotal;
        public int FilesDone { get; set; }
    }
}
