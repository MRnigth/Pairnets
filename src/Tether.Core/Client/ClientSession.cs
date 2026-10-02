using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tether.Core.Api;
using Tether.Core.Paths;
using Tether.Core.Settings;
using Tether.Core.State;
using Tether.Core.Sync;

namespace Tether.Core.Client;

/// <summary>
/// One running sync session for the desktop apps (Windows and macOS/Linux): state database, API
/// client, engine and runner, plus an activity feed and a status snapshot for the UI. All UI
/// frameworks share this class so the apps only draw and forward clicks.
/// </summary>
public sealed class ClientSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private StatusSnapshot _status = StatusSnapshot.Initial;

    /// <summary>A pass starting within this time of the previous one continues its file count.</summary>
    private static readonly TimeSpan BurstGap = TimeSpan.FromSeconds(15);
    private int _burstOffset;
    private int _passDone;
    private int _passTotal;
    private DateTimeOffset? _lastPassEnd;
    private readonly Dictionary<string, ActiveTransfer> _active = new(StringComparer.Ordinal);
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private long _burstBytesDone;
    private long _burstBytesTotal;
    private Timer? _infoTimer;

    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServerInfoInterval = TimeSpan.FromMinutes(5);

    /// <summary>Bytes per second over the last few seconds. Call with <c>_gate</c> held.</summary>
    private double RateLocked()
    {
        var now = _clock.GetTimestamp();
        while (_samples.Count > 0 && _clock.GetElapsedTime(_samples.Peek().Ticks, now) > SpeedWindow)
            _samples.Dequeue();
        if (_samples.Count == 0)
            return 0;
        var span = Math.Max(1.0, _clock.GetElapsedTime(_samples.Peek().Ticks, now).TotalSeconds);
        return _samples.Sum(x => (double)x.Bytes) / span;
    }

    /// <summary>Raised when the server's version, free space or updater state was (re)read.</summary>
    public event Action<ServerInfo>? ServerInfoChanged;

    /// <summary>Reads the server's version, free space and updater state; failures are ignored (offline is shown elsewhere).</summary>
    public async Task<ServerInfo?> RefreshServerInfoAsync()
    {
        try
        {
            var info = await Api.GetInfoAsync(CancellationToken.None).ConfigureAwait(false);
            Update(s => s with { Server = info });
            ServerInfoChanged?.Invoke(info);
            return info;
        }
        catch (Exception ex) when (ex is TetherNetworkException or TetherAuthException or TetherProtocolException or ObjectDisposedException)
        {
            return null;
        }
    }

    private ClientSession(ClientSettings settings, StateDb state, TetherApiClient api, SyncEngine engine, SyncRunner runner, TimeProvider clock)
    {
        Settings = settings;
        State = state;
        Api = api;
        Engine = engine;
        Runner = runner;
        _clock = clock;
    }

    public ClientSettings Settings { get; }
    public StateDb State { get; }
    public TetherApiClient Api { get; }
    public SyncEngine Engine { get; }
    public SyncRunner Runner { get; }
    public ActivityFeed Activity { get; } = new();

    public StatusSnapshot Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    /// <summary>Raised (on a background thread) whenever <see cref="Status"/> changes.</summary>
    public event Action<StatusSnapshot>? StatusChanged;

    public event Action<ConflictInfo>? ConflictCreated;

    public event Action<PathWarningInfo>? PathWarningRaised;

    public event Action<PassResult>? PassCompleted;

    public event Action<int>? CatchUpCompleted;

    /// <summary>Builds and starts a session. <paramref name="runnerOptions"/> overrides timings (tests).</summary>
    public static ClientSession Start(ClientSettings settings, string token, ILocalTrash trash, ILoggerFactory? loggers = null,
        string? stateBaseDir = null, Func<RunnerOptions, RunnerOptions>? runnerOptions = null, TimeProvider? clock = null,
        EngineHooks? hooks = null)
    {
        if (!settings.IsComplete)
            throw new InvalidOperationException("Settings are incomplete.");
        loggers ??= NullLoggerFactory.Instance;
        clock ??= TimeProvider.System;
        var folder = settings.Folder!;
        var stateDir = TetherPaths.StateDirFor(folder, stateBaseDir);
        var state = new StateDb(Path.Combine(stateDir, "state.db"));
        state.SetMeta(StateDb.MetaFolder, Path.GetFullPath(folder));
        var url = new Uri(settings.ServerUrl!);
        var api = new TetherApiClient(url, token, settings.DeviceName!);
        api.UploadLimit.SetMegabytesPerSecond(settings.UploadLimitMBps);
        api.DownloadLimit.SetMegabytesPerSecond(settings.DownloadLimitMBps);
        var engine = new SyncEngine(new EngineOptions
        {
            Folder = folder,
            DeviceName = settings.DeviceName!,
            ExtraIgnore = settings.ExtraIgnore,
            MaxParallelTransfers = settings.EffectiveParallelTransfers,
        }, api, state, trash, loggers.CreateLogger("Tether.Engine"), clock, hooks);
        var options = new RunnerOptions { ServerUrl = url, Token = token, DeviceId = settings.DeviceName!, WaitForPeerBatches = settings.WaitForPeerBatches };
        if (runnerOptions is not null)
            options = runnerOptions(options);
        var runner = new SyncRunner(engine, options, loggers.CreateLogger("Tether.Runner"));

        var session = new ClientSession(settings, state, api, engine, runner, clock);
        session.Wire();
        session._status = session._status with { LimitText = settings.LimitText };
        runner.Start();
        session._infoTimer = new Timer(_ => _ = session.RefreshServerInfoAsync(), null, TimeSpan.Zero, ServerInfoInterval);
        if (settings.Paused)
            runner.Pause();
        return session;
    }

    private void Wire()
    {
        Engine.FileSynced += (action, path) =>
        {
            var (kind, verb) = action switch
            {
                SyncAction.Upload => (ActivityKind.Uploaded, "Uploaded"),
                SyncAction.Download => (ActivityKind.Downloaded, "Downloaded"),
                SyncAction.DeleteLocal => (ActivityKind.DeletedHere, "Deleted here (deleted on another PC)"),
                _ => (ActivityKind.DeletedOnServer, "Deleted on the server"),
            };
            Activity.Add(kind, path, $"{verb}: {path}", _clock);
        };
        Engine.ConflictCreated += c =>
        {
            Activity.Add(ActivityKind.Conflict, c.ConflictCopyPath, $"Conflict on {c.Path}: your version was kept as {PathRules.FileName(c.ConflictCopyPath)}", _clock);
            ConflictCreated?.Invoke(c);
        };
        Engine.PathWarningRaised += w =>
        {
            Activity.Add(ActivityKind.Warning, w.Path, $"{w.Path}: {w.Message}", _clock);
            PathWarningRaised?.Invoke(w);
        };
        // File counts run across back-to-back passes ("Sync now" while files keep arriving): a pass
        // that starts soon after the previous one continues its count, and files that arrive during a
        // pass are added to the total straight away.
        Engine.ExecutionStarting += (_, _, bytes) =>
        {
            lock (_gate)
            {
                var chained = _lastPassEnd is { } end && _clock.GetUtcNow() - end < BurstGap;
                _burstOffset = chained ? _burstOffset + _passDone : 0;
                _burstBytesTotal = chained ? _burstBytesTotal + bytes : bytes;
                if (!chained)
                {
                    _burstBytesDone = 0;
                    _samples.Clear();
                }
                _passDone = 0;
                _passTotal = 0;
                _active.Clear();
            }
        };
        Engine.Progress += p =>
        {
            int done, total;
            IReadOnlyList<ActiveTransfer> active;
            long bytesDone, bytesTotal;
            double rate;
            lock (_gate)
            {
                _passDone = p.FilesDone;
                _passTotal = p.FilesTotal;
                done = _burstOffset + p.FilesDone;
                total = _burstOffset + p.FilesTotal + Runner.PendingChanges;
                if (p.CurrentPath is not null && p.Operation is "upload" or "download")
                {
                    var before = _active.TryGetValue(p.CurrentPath, out var a) ? a.BytesDone : 0;
                    var delta = p.BytesDone - before;
                    if (delta > 0)
                    {
                        _burstBytesDone += delta;
                        _samples.Enqueue((_clock.GetTimestamp(), delta));
                    }
                    _active[p.CurrentPath] = new ActiveTransfer(p.CurrentPath, p.Operation, p.BytesDone, p.BytesTotal);
                }
                active = [.. _active.Values];
                bytesDone = _burstBytesDone;
                bytesTotal = Math.Max(_burstBytesTotal, _burstBytesDone);
                rate = RateLocked();
            }
            Update(s => (p.CurrentPath is null
                ? s with { CurrentPath = null, Operation = null, BytesDone = 0, BytesTotal = 0, FilesDone = done, FilesTotal = total }
                : s with { CurrentPath = p.CurrentPath, Operation = p.Operation, BytesDone = p.BytesDone, BytesTotal = p.BytesTotal, FilesDone = done, FilesTotal = total })
                with { Active = active, PassBytesDone = bytesDone, PassBytesTotal = bytesTotal, BytesPerSecond = rate });
        };
        Engine.TransferFinished += path =>
        {
            IReadOnlyList<ActiveTransfer> active;
            lock (_gate)
            {
                _active.Remove(path);
                active = [.. _active.Values];
            }
            Update(s => s with { Active = active });
        };
        Runner.PendingChangesChanged += pending =>
        {
            int total;
            lock (_gate)
                total = _burstOffset + _passTotal + pending;
            Update(s => s with { FilesTotal = Math.Max(total, s.FilesDone) });
        };
        Runner.PeerWaitChanged += wait => Update(s => s with { WaitingFor = wait });
        Runner.StatusChanged += (status, text) => Update(s => s with
        {
            Status = status,
            Text = text,
            LastSyncAt = Runner.LastSyncAt,
            CurrentPath = status == RunnerStatus.Syncing ? s.CurrentPath : null,
            Paused = status == RunnerStatus.Paused,
        });
        Runner.PassCompleted += report =>
        {
            lock (_gate)
                _lastPassEnd = _clock.GetUtcNow();
            var r = report.Result;
            Update(s => s with
            {
                LastSyncAt = Runner.LastSyncAt,
                BlockReason = r.Outcome == PassOutcome.Blocked ? r.BlockReason : BlockReason.None,
                PendingDeletes = r.BlockedDeletes.Count,
                Warnings = SafeWarningCount(),
                CurrentPath = null,
            });
            switch (r.Outcome)
            {
                case PassOutcome.Blocked:
                    Activity.Add(ActivityKind.Blocked, null, r.Message ?? r.BlockReason.ToString(), _clock);
                    break;
                case PassOutcome.Offline when Activity.Items.FirstOrDefault()?.Kind != ActivityKind.Offline:
                    Activity.Add(ActivityKind.Offline, null, "Cannot reach the server; retrying. " + r.Message, _clock);
                    break;
                case PassOutcome.AuthFailed:
                    Activity.Add(ActivityKind.Error, null, "The server rejected the token. Open Settings and enter it again.", _clock);
                    break;
                case PassOutcome.Failed:
                    Activity.Add(ActivityKind.Error, null, "Sync failed: " + r.Message, _clock);
                    break;
                case PassOutcome.Completed when r.Errors > 0:
                    foreach (var message in r.ErrorMessages.Take(5))
                        Activity.Add(ActivityKind.Error, null, message, _clock);
                    break;
            }
            PassCompleted?.Invoke(r);
        };
        Runner.CatchUpCompleted += n =>
        {
            Activity.Add(ActivityKind.Info, null, $"Caught up: {n} change(s) synced", _clock);
            CatchUpCompleted?.Invoke(n);
        };
    }

    private int SafeWarningCount()
    {
        try
        {
            return State.LoadWarnings().Count;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return 0;
        }
    }

    private void Update(Func<StatusSnapshot, StatusSnapshot> change)
    {
        StatusSnapshot next;
        lock (_gate)
        {
            next = change(_status);
            if (next == _status)
                return;
            _status = next;
        }
        StatusChanged?.Invoke(next);
    }

    // ------------------------------------------------------------------ actions for the UI

    public void SyncNow() => Runner.RequestSync("manual", full: true);

    public void Pause()
    {
        Settings.Paused = true;
        Runner.Pause();
    }

    public void Resume()
    {
        Settings.Paused = false;
        Runner.Resume();
    }

    public int ApproveDeletions()
    {
        var n = Runner.ApproveDeletions();
        Activity.Add(ActivityKind.Info, null, $"You allowed {n} deletion(s)", _clock);
        return n;
    }

    public void AdoptExistingMarker()
    {
        Engine.AdoptExistingMarker();
        Activity.Add(ActivityKind.Info, null, "Folder confirmed; merging without deleting", _clock);
        Runner.RequestSync("adopted", full: true);
    }

    public void RelinkToServer()
    {
        Engine.RelinkToServer();
        Activity.Add(ActivityKind.Info, null, "Re-linked to the server; merging without deleting", _clock);
        Runner.RequestSync("relinked", full: true);
    }

    public IReadOnlyList<PathWarning> Warnings() => State.LoadWarnings().Values.OrderBy(w => w.Path, StringComparer.Ordinal).ToList();

    public IReadOnlyList<(DeleteSide Side, string Path)> PendingDeletes() => Engine.PendingDeletes;

    /// <summary>Text for the "allow deletions?" confirmation, listing what would be deleted.</summary>
    public string DescribePendingDeletes(int max = 20)
    {
        var pending = PendingDeletes();
        var local = pending.Count(p => p.Side == DeleteSide.Local);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Tether stopped because this sync would delete {pending.Count} file(s):");
        sb.AppendLine($"  {local} on this computer (deleted on the other one)");
        sb.AppendLine($"  {pending.Count - local} on the server (deleted on this computer)");
        sb.AppendLine();
        foreach (var (side, path) in pending.Take(max))
            sb.AppendLine($"  {(side == DeleteSide.Local ? "this computer" : "server")}: {path}");
        if (pending.Count > max)
            sb.AppendLine($"  … and {pending.Count - max} more");
        sb.AppendLine();
        sb.AppendLine("If this is unexpected (wrong folder, unplugged drive), say No and check the folder.");
        sb.AppendLine("Deleted files stay recoverable from the server history for 30 days.");
        sb.AppendLine();
        sb.Append("Allow these deletions once?");
        return sb.ToString();
    }

    /// <summary>Directory holding this session's state.db.</summary>
    public string StateDirectory => Path.GetDirectoryName(State.DatabasePath)!;

    /// <summary>
    /// "Locate the sync folder", step 1: checks that <paramref name="newFolder"/> is this session's
    /// folder in a new place (same marker). Returns the error to show, or null if it matches.
    /// Step 2, after disposing the session: <see cref="StateLocator.AdoptState"/> with
    /// <see cref="StateDirectory"/>, save the new folder in settings, start a new session.
    /// </summary>
    public string? CheckMovedFolder(string newFolder)
    {
        var marker = StateLocator.ReadMarker(newFolder);
        if (marker is null || !string.Equals(marker, State.MarkerId, StringComparison.OrdinalIgnoreCase))
            return "That folder is not your Tether folder (its .tether-marker is missing or different). Nothing was changed.";
        return null;
    }

    /// <summary>True when the server runs an older release than this app and the user did not skip that version.</summary>
    public bool ServerNeedsUpdate(ServerInfo? info = null)
    {
        info ??= Status.Server;
        return info?.ServerVersion is { } v && UpdateChecker.ServerIsOlder(v, TetherInfo.ProductVersion)
            && !string.Equals(v, Settings.SkippedServerVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Update server": asks the server to update itself, then follows it (it restarts on the way)
    /// until it reports a newer version, the updater reports a failure, or <paramref name="timeout"/> passes.
    /// </summary>
    public async Task<ServerUpdateResult> UpdateServerAsync(IProgress<string>? progress, CancellationToken ct,
        TimeSpan? pollInterval = null, TimeSpan? timeout = null)
    {
        var before = Status.Server?.ServerVersion;
        TetherApiClient.ServerUpdateRequest request;
        try
        {
            request = await Api.RequestServerUpdateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TetherNetworkException or TetherAuthException or TetherProtocolException)
        {
            return new ServerUpdateResult(false, "Could not reach the server: " + ex.Message, before, CanUpdateItself: true);
        }
        switch (request)
        {
            case TetherApiClient.ServerUpdateRequest.UpdaterMissing or TetherApiClient.ServerUpdateRequest.NotSupported:
                return new ServerUpdateResult(false, "This server can't update itself yet. Run the install command on the server once.", before, CanUpdateItself: false);
            case TetherApiClient.ServerUpdateRequest.TooSoon:
                return new ServerUpdateResult(false, "An update was just requested. Wait a minute and try again.", before, CanUpdateItself: true);
        }

        progress?.Report("Downloading and checking the new version");
        var deadline = _clock.GetUtcNow() + (timeout ?? TimeSpan.FromMinutes(3));
        var restarting = false;
        while (_clock.GetUtcNow() < deadline)
        {
            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(3), _clock, ct).ConfigureAwait(false);
            ServerInfo info;
            try
            {
                info = await Api.GetInfoAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TetherNetworkException or TetherProtocolException)
            {
                if (!restarting)
                    progress?.Report("Installing and restarting the server");
                restarting = true;
                continue;
            }
            Update(s => s with { Server = info });
            if (info.ServerVersion is { } now && UpdateChecker.IsNewer(now, before))
            {
                ServerInfoChanged?.Invoke(info);
                return new ServerUpdateResult(true, $"Server updated to {now}.", now, CanUpdateItself: true);
            }
            switch (info.Updater?.State)
            {
                case "failed":
                    return new ServerUpdateResult(false, info.Updater.Message ?? "The update failed. Nothing was changed.", before, CanUpdateItself: true);
                case "succeeded":
                    return new ServerUpdateResult(info.ServerVersion != before, info.Updater.Message ?? "Done.", info.ServerVersion, CanUpdateItself: true);
                case "running":
                    progress?.Report(info.Updater.Message ?? "Installing");
                    break;
            }
        }
        return new ServerUpdateResult(false, "The server did not finish updating in time. Check it with: sudo journalctl -u tether-update -n 50", before, CanUpdateItself: true);
    }

    /// <summary>"Download now anyway": stop waiting for another computer's big batch.</summary>
    public void DownloadNow() => Runner.ReleaseHold();

    public async ValueTask DisposeAsync()
    {
        if (_infoTimer is not null)
            await _infoTimer.DisposeAsync().ConfigureAwait(false);
        await Runner.DisposeAsync().ConfigureAwait(false);
        Api.Dispose();
        State.Dispose();
    }
}

/// <summary>How "Update server" ended. <see cref="CanUpdateItself"/> false: show the one-time install command.</summary>
public sealed record ServerUpdateResult(bool Success, string Message, string? ServerVersion, bool CanUpdateItself);
