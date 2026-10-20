using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Core.State;

namespace Pairnets.Core.Sync;

/// <summary>
/// Runs sync passes for one folder. A pass recomputes everything from disk, the state database and
/// the server manifest, so an interrupted pass is always repaired by the next one.
/// Not thread-safe: the <see cref="SyncRunner"/> guarantees that only one pass runs at a time.
/// </summary>
public sealed class SyncEngine
{
    private readonly EngineOptions _options;
    private readonly IPairnetsApi _api;
    private readonly StateDb _state;
    private readonly ILocalTrash _trash;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;
    private readonly EngineHooks _hooks;
    private readonly LocalScanner _scanner;
    private readonly ICloudFiles _cloud;
    private readonly ConcurrentDictionary<string, long> _recentlyTouched = new(StringComparer.Ordinal);

    public SyncEngine(EngineOptions options, IPairnetsApi api, StateDb state, ILocalTrash? trash = null,
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
        _cloud = options.CloudFiles;
        _scanner = new LocalScanner(Root, Ignore, state, _clock, _log)
        {
            StabilityWindow = options.StabilityWindow,
            RacyWindow = options.RacyWindow,
            Progress = p => ScanProgressed?.Invoke(p),
            CloudFiles = options.CloudFiles,
        };
    }

    /// <summary>How far the look through the folder is, while a pass is still finding out what changed.</summary>
    public event Action<ScanProgress>? ScanProgressed;

    public string Root { get; }

    public IgnoreList Ignore { get; }

    public string DeviceName => _options.DeviceName;

    public StateDb State => _state;

    public string MarkerPath => Path.Combine(Root, PathRules.MarkerFileName);

    private volatile bool _signInRequired;

    /// <summary>
    /// Set while this computer still uses the shared token although its nest signs computers in: every
    /// pass is blocked (<see cref="BlockReason.SignInRequired"/>) until a person signs it in with the
    /// browser. Nothing on disk is touched meanwhile.
    /// </summary>
    public bool SignInRequired
    {
        get => _signInRequired;
        set => _signInRequired = value;
    }

    public string TempDirectory => Path.Combine(Root, PathRules.TempFolderName);

    public event Action<SyncProgress>? Progress;

    public event Action<ConflictInfo>? ConflictCreated;

    public event Action<PathWarningInfo>? PathWarningRaised;

    /// <summary>Raised after a file was uploaded, downloaded or deleted (for activity lists). May be raised concurrently.</summary>
    public event Action<SyncAction, string>? FileSynced;

    /// <summary>Raised once per pass after planning: uploads (incl. conflicts), downloads, and the bytes they will move.</summary>
    public event Action<int, int, long>? ExecutionStarting;

    /// <summary>
    /// Awaited right after <see cref="ExecutionStarting"/>, before the first transfer, with the number of uploads: the
    /// runner announces a big batch to the other computers here. Awaited, so a slow push channel holds no thread.
    /// </summary>
    internal Func<int, CancellationToken, Task>? BeforeExecution { get; set; }

    /// <summary>
    /// Raised once per pass right after <see cref="ExecutionStarting"/>: the uploads and downloads of the pass in
    /// the order they run, each as its path and "upload" or "download". Deletions and conflicts are left out (a
    /// conflict's copy and download show up in <see cref="Progress"/> when they start).
    /// </summary>
    public event Action<IReadOnlyList<(string Path, string Operation)>>? ExecutionPlanned;

    /// <summary>Raised when an upload or download (finished, failed or skipped) no longer runs.</summary>
    public event Action<string>? TransferFinished;

    /// <summary>
    /// Raised when a pass moves on to another stage, and while it checks the folder: (stage, done, total). For
    /// <see cref="SyncStage.Checking"/> done and total are files (total 0 while the folder is still being listed).
    /// </summary>
    public event Action<SyncStage, int, int>? StageChanged;

    /// <summary>
    /// Raised when the server took a change from this computer (an upload or a deletion), with the server version
    /// it got; another computer that reports having that version has the change. May be raised concurrently.
    /// </summary>
    public event Action<long>? ChangeSent;

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
        var marker = ReadMarkerFile() ?? throw new InvalidOperationException("The folder has no readable .pairnets-marker.");
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
        catch (PairnetsNetworkException ex)
        {
            result.Outcome = PassOutcome.Offline;
            result.Message = FriendlyErrors.Describe(ex);
            _log.LogWarning("Pass aborted, server unreachable: {Error}", ex.Message);
        }
        catch (PairnetsAuthException ex) when (ex.NeedsSignIn)
        {
            // Not a typo in Settings: the nest no longer lets this computer in. Nothing is retried until it signs in again.
            result.Outcome = PassOutcome.Blocked;
            result.BlockReason = BlockReason.SignedOut;
            result.Message = FriendlyErrors.Describe(ex);
            _log.LogWarning("Pass stopped: {Error}", ex.Message);
        }
        catch (PairnetsAuthException ex)
        {
            result.Outcome = PassOutcome.AuthFailed;
            result.Message = FriendlyErrors.Describe(ex);
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
                result.Message = FriendlyErrors.Describe(ex);
            }
            _log.LogWarning("Pass aborted, scan failed: {Error}", ex.Message);
        }
        catch (PairnetsProtocolException ex)
        {
            result.Outcome = PassOutcome.Failed;
            result.Message = FriendlyErrors.Describe(ex);
            _log.LogError("Pass aborted, unexpected server answer: {Error}", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            result.Outcome = PassOutcome.Failed;
            result.Message = FriendlyErrors.Describe(ex);
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
        // ---- 0. A computer the nest wants signed in does nothing until a person signs it in.
        if (SignInRequired)
        {
            Block(result, BlockReason.SignInRequired, "Your nest now signs computers in. Sign in with your browser to keep syncing; your files stay as they are.");
            return;
        }

        // ---- 1. Folder and marker: never mistake a missing or wrong folder for "everything was deleted".
        if (!Directory.Exists(Root))
        {
            Block(result, BlockReason.FolderMissing, $"The sync folder {Root} is missing. Is the drive connected?");
            return;
        }

        // A folder synced as Tether: its marker becomes Pairnets' (same folder id), so nothing is blocked.
        Legacy.TetherMigration.MigrateSyncFolder(Root);
        var tracked = _state.CountTracked();
        var stateMarker = _state.MarkerId;
        var fileMarker = ReadMarkerFile();
        if (stateMarker is null)
        {
            if (fileMarker is not null)
            {
                Block(result, BlockReason.ForeignMarker,
                    "This folder was already synced by Pairnets with a different state. Confirm before syncing it again.");
                return;
            }
            stateMarker = CreateMarker();
        }
        else if (fileMarker is null)
        {
            Block(result, BlockReason.MarkerMissing,
                $"The folder {Root} has no .pairnets-marker although it was synced before. Wrong folder or drive?");
            return;
        }
        else if (!string.Equals(fileMarker, stateMarker, StringComparison.OrdinalIgnoreCase))
        {
            Block(result, BlockReason.MarkerMismatch, $"The marker in {Root} belongs to a different sync state.");
            return;
        }

        CleanTempDirectory();

        // ---- 2. Server manifest (full or delta) into the local mirror.
        StageChanged?.Invoke(SyncStage.ReadingServer, 0, 0);
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
        result.ServerVersion = cursor;

        // ---- 3. Local scan.
        StageChanged?.Invoke(SyncStage.Checking, 0, 0);
        var scan = await _scanner.ScanAsync(ct, (done, total) => StageChanged?.Invoke(SyncStage.Checking, done, total)).ConfigureAwait(false);
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
            if (action == SyncAction.DeleteRemote && b is not null && scan.HeldVersions.Contains(b))
            {
                result.Deferred++;
                continue; // a file left on the nest was renamed or moved here: its only copy stays until it is opened
            }
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
        await DropStaleTransfersAsync(plan, scan).ConfigureAwait(false);

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
            result.HeldDownloads += deferred; // not "Deferred": no quick retry, the hold ending triggers the pass
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
        if (ordered.Count > 0)
            StageChanged?.Invoke(SyncStage.Transferring, 0, ordered.Count);
        var uploads = ordered.Count(p => p.Action is SyncAction.Upload or SyncAction.Conflict);
        ExecutionStarting?.Invoke(uploads, ordered.Count(p => p.Action == SyncAction.Download), plannedBytes);
        if (BeforeExecution is { } before)
            await before(uploads, ct).ConfigureAwait(false);
        ExecutionPlanned?.Invoke(ordered
            .Where(p => p.Action is SyncAction.Upload or SyncAction.Download)
            .Select(p => (p.Path, p.Action == SyncAction.Upload ? "upload" : "download"))
            .ToList());

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
            for (var slowDowns = 0; ; slowDowns++)
            {
                try
                {
                    await ExecuteAsync(item, ctx, ct).ConfigureAwait(false);
                    break;
                }
                catch (PairnetsSlowDownException ex) when (item.Action is SyncAction.Upload or SyncAction.Download
                    && slowDowns < PairnetsApiClient.MaxSlowDownsInARow && !ct.IsCancellationRequested)
                {
                    // Too many requests from this address: the API client holds every request back for the time the
                    // server asked, so the same file simply goes again. A big upload or download goes on where it was.
                    _log.LogDebug("{Action} of {Path} waits: {Error}", item.Action, item.Path, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PairnetsProtocolException)
        {
            lock (ctx.Gate)
            {
                ctx.Result.Errors++;
                ctx.Result.ErrorMessages.Add($"{item.Path}: {FriendlyErrors.Describe(ex)}");
            }
            _log.LogWarning("{Action} failed for {Path}: {Error}", item.Action, item.Path, FriendlyErrors.Technical(ex));
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
                if (_cloud.IsActive && item.Local is { } recorded)
                    _cloud.MarkSynced(ResolveLocal(item.Path), recorded);
                lock (ctx.Gate)
                    ctx.Result.Recorded++;
                return Task.CompletedTask;
            case SyncAction.ClearBase:
                _state.RemoveFile(item.Path);
                return Task.CompletedTask;
            case SyncAction.Upload:
                return UploadAsync(item.Path, item.Server ?? ContentHash.NoneBase, item.Local, ctx, ct);
            case SyncAction.Download:
                return DownloadAsync(item.Path, item.Local, item.Server, ctx, ct);
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

    private async Task UploadAsync(string path, string baseHash, string? localHash, PassContext ctx, CancellationToken ct)
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
            var resume = await ResumeForAsync(path, baseHash, localHash, total, mtimeTicks).ConfigureAwait(false);
            Report(path, "upload", 0, total, ctx);
            (ApiResult Result, string SentHash, long SentBytes) sent;
            try
            {
                sent = await _api.UploadAsync(path, baseHash, mtimeMs, stream,
                    bytes => Report(path, "upload", bytes, total, ctx), ct, resume).ConfigureAwait(false);
            }
            finally
            {
                // Kept while the server holds the unfinished upload (a broken connection, Pause, closing the app),
                // so the next pass continues it; forgotten once it is finished or the server no longer has it.
                if (resume.Id is null)
                    _state.RemoveUpload(path);
                else
                    _log.LogInformation("Upload of {Path} stopped before it was finished; the next pass continues it", path);
            }
            if (resume.ResumedFrom > 0)
                _log.LogInformation("Continued the upload of {Path} at {Bytes} of {Size} bytes", path, resume.ResumedFrom, total);
            var (res, sentHash, sentBytes) = sent;
            switch (res.Outcome)
            {
                case ApiOutcome.Ok:
                    var entry = res.Entry!;
                    if (!string.Equals(entry.Hash, sentHash, StringComparison.Ordinal))
                        throw new PairnetsProtocolException($"Server stored different content for {path}.");
                    info.Refresh();
                    var unchanged = info.Exists && info.Length == sentBytes && info.LastWriteTimeUtc.Ticks == mtimeTicks;
                    _state.SetSynced(path, entry.Hash!, unchanged ? sentBytes : -1, unchanged ? mtimeTicks : 0,
                        unchanged ? sentHash : null, _clock.GetUtcNow().UtcTicks);
                    if (unchanged)
                        _cloud.MarkSynced(full, entry.Hash!);
                    _state.UpsertRemote(entry);
                    _state.ClearWarning(path);
                    lock (ctx.Gate)
                        ctx.Result.Uploaded++;
                    FileSynced?.Invoke(SyncAction.Upload, path);
                    ChangeSent?.Invoke(entry.Version);
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
                    throw new PairnetsProtocolException($"Unexpected upload result {res.Outcome} for {path}.");
            }
        }
    }

    /// <summary>
    /// The unfinished upload a pass before this one left for exactly this content (same size, modification time and
    /// content hash), base and server, to be continued; one for anything else is given up. A session the upload
    /// opens is written to state.db at once, so it survives Pause and a restart of the app.
    /// </summary>
    private async Task<UploadResume> ResumeForAsync(string path, string baseHash, string? localHash, long size, long mtimeTicks)
    {
        var server = _api.ServerKey;
        string? id = null;
        if (_state.GetUpload(path) is { } saved)
        {
            if (localHash is not null && saved.Server == server && saved.BaseHash == baseHash
                && saved.Size == size && saved.MtimeTicks == mtimeTicks && saved.Hash == localHash)
                id = saved.UploadId;
            else
                await AbandonUploadAsync(saved, "the file, or its version on the server, changed since").ConfigureAwait(false);
        }
        return new UploadResume(id, localHash is null ? null : newId =>
            _state.SetUpload(new UploadCheckpoint(path, newId, server, baseHash, size, mtimeTicks, localHash, _clock.GetUtcNow().ToUnixTimeMilliseconds())));
    }

    /// <summary>Gives up an unfinished upload for good: the server drops its part, and the next upload of the file starts from the beginning.</summary>
    private async Task AbandonUploadAsync(UploadCheckpoint saved, string why)
    {
        _log.LogInformation("Giving up the unfinished upload of {Path}: {Why}", saved.Path, why);
        if (string.Equals(saved.Server, _api.ServerKey, StringComparison.Ordinal))
            await _api.AbandonUploadAsync(saved.UploadId).ConfigureAwait(false);
        _state.RemoveUpload(saved.Path, saved.UploadId);
    }

    /// <summary>
    /// Unfinished transfers this pass does not continue are given up: the file was deleted or changed, is in sync
    /// now, or the server has another version. Those of files that are unknown this pass (locked, still being
    /// written, in a folder that could not be read) wait for a later pass.
    /// </summary>
    private async Task DropStaleTransfersAsync(List<PlannedAction> plan, ScanResult scan)
    {
        var uploads = _state.LoadUploads();
        var downloads = _state.LoadDownloads();
        if (uploads.Count == 0 && downloads.Count == 0)
            return;
        var uploading = plan.Where(p => p.Action == SyncAction.Upload).Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var downloading = plan.Where(p => p.Action is SyncAction.Download or SyncAction.Conflict).Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        bool Unknown(string path) => scan.IsUnderUnknownPrefix(path) || (scan.Files.TryGetValue(path, out var f) && !f.Stable);
        foreach (var saved in uploads)
        {
            if (!uploading.Contains(saved.Path) && !Unknown(saved.Path))
                await AbandonUploadAsync(saved, "it is no longer sent").ConfigureAwait(false);
        }
        foreach (var partial in downloads)
        {
            if (!downloading.Contains(partial.Path) && !Unknown(partial.Path))
                DropPartialDownload(partial, "it is no longer downloaded");
        }
    }

    // ------------------------------------------------------------------ download

    private async Task DownloadAsync(string path, string? expectedLocalHash, string? serverHash, PassContext ctx, CancellationToken ct)
    {
        if (TryPlaceOnline(path, serverHash, ctx))
            return;
        var size = ctx.Sizes.GetValueOrDefault(path);
        // Big files keep what arrived when the connection breaks, and later ask only for the rest of the same version.
        if (serverHash is null || size < _options.ResumeDownloadsFrom || _hooks.WrapDownloadStream is not null)
        {
            await DownloadOnceAsync(path, expectedLocalHash, null, ctx, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            await DownloadOnceAsync(path, expectedLocalHash, PartialFor(path, serverHash, size), ctx, ct).ConfigureAwait(false);
        }
        catch (StartAgainException ex)
        {
            // What a try before this one left was not usable (it is deleted): download the whole file instead.
            _log.LogWarning("{Message} Downloading all of {Path} again", ex.Message, path);
            await DownloadOnceAsync(path, expectedLocalHash, PartialFor(path, serverHash, size), ctx, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Files On-Demand: a file from the nest that is not here yet (or only as a placeholder) arrives as a placeholder of
    /// the nest's version, downloaded only when it is opened. False when it must be downloaded now (switched off,
    /// "Always keep on this device", a file with local content, or Windows could not make the placeholder).
    /// </summary>
    private bool TryPlaceOnline(string path, string? serverHash, PassContext ctx)
    {
        if (!_cloud.IsActive || serverHash is null)
            return false;
        var full = ResolveLocal(path);
        if ((File.Exists(full) && !_cloud.IsCloudOnly(full)) || Directory.Exists(full) || !_cloud.KeepOnNest(full))
            return false;
        if (_state.GetRemote(path) is not { Deleted: false } remote || remote.Hash != serverHash)
            return false;
        EnsureParentDirectory(path, full);
        var modified = remote.ModifiedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(remote.ModifiedMs).UtcDateTime : _clock.GetUtcNow().UtcDateTime;
        if (!_cloud.PlaceOnline(full, serverHash, remote.Size, modified))
            return false;
        MarkTouched(path);
        var info = new FileInfo(full);
        _state.SetSynced(path, serverHash, info.Length, info.LastWriteTimeUtc.Ticks, serverHash, _clock.GetUtcNow().UtcTicks);
        lock (ctx.Gate)
            ctx.Result.Downloaded++;
        FileSynced?.Invoke(SyncAction.Download, path);
        _log.LogInformation("Placed {Path} from the nest ({Bytes} bytes, downloaded when opened)", path, remote.Size);
        return true;
    }

    /// <summary>
    /// The content hash of a local file. A file whose content is still on the nest is the version Pairnets last synced
    /// (its notes say which): reading it would only download it again.
    /// </summary>
    private async Task<string> LocalHashAsync(string path, string full, CancellationToken ct)
    {
        if (CloudPlaceholders.IsContentOnline(File.GetAttributes(full)) && _state.GetFile(path) is { Hash: { } cached })
            return cached;
        var (hash, _) = await ContentHash.OfFileAsync(full, ct).ConfigureAwait(false);
        return hash;
    }

    /// <summary>
    /// One download into a temp file in .pairnets-tmp, verified against the server's length and hash, then moved
    /// into place. With <paramref name="partial"/> the temp file is kept when the download is cut off (and recorded
    /// in state.db with the version its bytes belong to), and continued if it already holds a start.
    /// </summary>
    private async Task DownloadOnceAsync(string path, string? expectedLocalHash, PartialDownload? partial, PassContext ctx, CancellationToken ct)
    {
        var full = ResolveLocal(path);
        var tmp = Path.Combine(EnsureTempDirectory(), partial?.TmpName ?? Guid.NewGuid().ToString("N") + ".part");
        var keep = false;
        try
        {
            DownloadResult dl;
            await using (var fs = new FileStream(tmp, partial is null ? FileMode.CreateNew : FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, ContentHash.BufferSize, FileOptions.Asynchronous))
            {
                DownloadResume? resume = null;
                if (partial is not null)
                {
                    // What a try before this one left (one that is complete or longer is suspect: start again).
                    var have = fs.Length < partial.Size ? fs.Length : 0;
                    fs.SetLength(have);
                    fs.Position = have;
                    resume = new DownloadResume(partial.Hash, have);
                }
                var target = _hooks.WrapDownloadStream?.Invoke(fs) ?? fs;
                Report(path, "download", resume?.Offset ?? 0, 0, ctx);
                try
                {
                    dl = await _api.DownloadAsync(path, target, bytes => Report(path, "download", bytes, 0, ctx), ct, resume).ConfigureAwait(false);
                    await target.FlushAsync(ct).ConfigureAwait(false);
                    fs.Flush(flushToDisk: true);
                }
                catch (Exception ex) when (partial is not null && KeepsPartial(ex))
                {
                    // Cut off, paused or closing: keep what arrived, recorded with the version it belongs to.
                    fs.Flush(flushToDisk: true);
                    if (fs.Length > 0)
                    {
                        _state.SetDownload(partial with
                        {
                            Hash = resume!.ServedHash ?? partial.Hash,
                            Size = resume.ServedSize ?? partial.Size,
                        });
                        keep = true;
                        _log.LogInformation("Download of {Path} stopped at {Bytes} bytes; the next pass continues it", path, fs.Length);
                    }
                    throw;
                }
                catch (Exception ex) when (resume is { Offset: > 0 } && ex is DownloadMismatchException or PairnetsProtocolException)
                {
                    throw new StartAgainException(ex.Message, ex);
                }
                if (resume is { ContinuedFrom: > 0 })
                    _log.LogInformation("Continued the download of {Path} at {Bytes} bytes", path, resume.ContinuedFrom);
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
            _cloud.MarkSynced(full, dl.Hash);
            lock (ctx.Gate)
                ctx.Result.Downloaded++;
            FileSynced?.Invoke(SyncAction.Download, path);
            _log.LogInformation("Downloaded {Path} ({Bytes} bytes)", path, dl.Size);
        }
        finally
        {
            if (!keep)
            {
                TryDeleteFile(tmp);
                if (partial is not null)
                    _state.RemoveDownload(path);
            }
        }
    }

    /// <summary>A cut connection, Pause or closing the app keeps a partial download; a wrong answer or a local error does not.</summary>
    private static bool KeepsPartial(Exception ex) =>
        ex is OperationCanceledException or PairnetsAuthException
        || (ex is PairnetsNetworkException && ex is not DownloadMismatchException);

    /// <summary>
    /// The partial download of <paramref name="path"/> to continue: the one a pass before this one left for this
    /// server and exactly this version, or a new one (recorded before any byte arrives). One of another version is deleted.
    /// </summary>
    private PartialDownload PartialFor(string path, string serverHash, long size)
    {
        var server = _api.ServerKey;
        if (_state.GetDownload(path) is { } saved)
        {
            if (saved.Server == server && saved.Hash == serverHash && IsTempName(saved.TmpName))
                return saved;
            DropPartialDownload(saved, "the file changed on the server since");
        }
        var fresh = new PartialDownload(path, Guid.NewGuid().ToString("N") + ".part", server, serverHash, size, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        _state.SetDownload(fresh);
        return fresh;
    }

    private void DropPartialDownload(PartialDownload partial, string why)
    {
        _log.LogInformation("Deleting the unfinished download of {Path}: {Why}", partial.Path, why);
        if (IsTempName(partial.TmpName))
            TryDeleteFile(Path.Combine(TempDirectory, partial.TmpName));
        _state.RemoveDownload(partial.Path);
    }

    /// <summary>A plain file name in .pairnets-tmp (never a path that could point elsewhere).</summary>
    private static bool IsTempName(string name) =>
        name.Length > 0 && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) && name.EndsWith(".part", StringComparison.Ordinal);

    /// <summary>A partial download could not be continued (its bytes did not match, or the server answered oddly); it was deleted.</summary>
    private sealed class StartAgainException(string message, Exception inner) : Exception(message, inner);

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
        var current = await LocalHashAsync(path, full, ct).ConfigureAwait(false);
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
        await UploadAsync(copy, ContentHash.NoneBase, item.Local, ctx, ct).ConfigureAwait(false);

        // 3. Bring the server version to the original name.
        await DownloadAsync(item.Path, expectedLocalHash: null, item.Server, ctx, ct).ConfigureAwait(false);
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
        var current = await LocalHashAsync(item.Path, full, ct).ConfigureAwait(false);
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
                if (res.Entry is not null)
                    ChangeSent?.Invoke(res.Entry.Version);
                _log.LogInformation("Deleted {Path} on the server (deleted here)", item.Path);
                break;
            case ApiOutcome.Conflict:
                lock (ctx.Gate)
                    ctx.Result.Deferred++;
                _log.LogInformation("Delete of {Path} deferred: the server changed meanwhile", item.Path);
                break;
            default:
                throw new PairnetsProtocolException($"Unexpected delete result {res.Outcome} for {item.Path}.");
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
        if (PathRules.HasReparsePoint(Root, path, cloudFolder: true))
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

    /// <summary>Empties .pairnets-tmp, except the partial downloads that state.db says a later pass may continue.</summary>
    private void CleanTempDirectory()
    {
        var dir = TempDirectory;
        if (!Directory.Exists(dir))
            return;
        var kept = _state.LoadDownloads().Select(d => d.TmpName).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!kept.Contains(Path.GetFileName(file)))
                TryDeleteFile(file);
        }
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
