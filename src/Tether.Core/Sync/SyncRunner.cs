using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Tether.Core.Paths;

namespace Tether.Core.Sync;

public enum RunnerStatus
{
    Idle,
    Syncing,
    Offline,
    Error,
    Blocked,
    Paused,
}

/// <summary>Runner configuration.</summary>
public sealed class RunnerOptions
{
    public required Uri ServerUrl { get; init; }
    public required string Token { get; init; }
    public required string DeviceId { get; init; }
    public TimeSpan WatcherDebounce { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan WatcherMaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RemoteDebounce { get; init; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan PeriodicInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan UnstableRetry { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ErrorRetry { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AuthRetry { get; init; } = TimeSpan.FromMinutes(1);
    public IReadOnlyList<TimeSpan> OfflineBackoff { get; init; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    public bool EnableWatcher { get; init; } = true;
    public bool EnableHub { get; init; } = true;

    /// <summary>A pass that transfers at least this many files after startup or an offline period raises CatchUpCompleted.</summary>
    public int CatchUpThreshold { get; init; } = 10;

    /// <summary>While another computer uploads a big batch, wait and download it in one go afterwards.</summary>
    public bool WaitForPeerBatches { get; init; } = true;

    /// <summary>A pass with at least this many uploads is announced to the other computers as a big batch.</summary>
    public int BatchThreshold { get; init; } = 100;

    /// <summary>Stop waiting for a batch when its computer sends nothing for this long.</summary>
    public TimeSpan PeerQuietTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Never wait for one batch longer than this.</summary>
    public TimeSpan MaxHold { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How often the two timeouts above are checked.</summary>
    public TimeSpan HoldCheckInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Another computer is uploading a big batch; this one waits. <see cref="Seen"/> files of it reached the server so far.</summary>
public sealed record PeerWait(string Device, int Count, int Seen);

/// <summary>A completed pass and why it ran.</summary>
public sealed record PassReport(PassResult Result, IReadOnlyCollection<string> Reasons, DateTimeOffset FinishedAt);

/// <summary>
/// Decides when passes run: at startup, on (debounced) local file changes, on SignalR pushes from
/// the other device, every few minutes, after resume, and on demand. Exactly one pass runs at a
/// time; requests arriving meanwhile are merged into one follow-up pass.
/// </summary>
public sealed class SyncRunner : IAsyncDisposable
{
    private static readonly TimeSpan OwnEventWindow = TimeSpan.FromSeconds(5);

    private readonly SyncEngine _engine;
    private readonly RunnerOptions _options;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _pendingReasons = [];
    private DateTimeOffset? _dueAt;
    private DateTimeOffset? _firstWatcherRequest;
    private bool _fullRequested;
    private bool _paused;
    private CancellationTokenSource? _passCts;
    private readonly HashSet<string> _arrivedDuringPass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PeerBatchState> _peerBatches = new(StringComparer.Ordinal);
    private bool _announced;
    private Timer? _holdTimer;

    private sealed class PeerBatchState(int count, DateTimeOffset now)
    {
        public int Count { get; } = count;
        public int Seen { get; set; }
        public DateTimeOffset Started { get; } = now;
        public DateTimeOffset LastActivity { get; set; } = now;
    }
    private bool _catchUpArmed = true;
    private int _offlineAttempts;
    private Task? _loop;
    private Task? _hubLoop;
    private FileSystemWatcher? _watcher;
    private HubConnection? _hub;
    private Timer? _periodic;

    public SyncRunner(SyncEngine engine, RunnerOptions options, ILogger? log = null)
    {
        _engine = engine;
        _options = options;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _engine.ExecutionStarting += OnExecutionStarting;
    }

    public RunnerStatus Status { get; private set; } = RunnerStatus.Idle;

    public string StatusText { get; private set; } = "Starting";

    public DateTimeOffset? LastSyncAt { get; private set; }

    public PassResult? LastResult { get; private set; }

    public bool HubConnected => _hub?.State == HubConnectionState.Connected;

    public SyncEngine Engine => _engine;

    public event Action<RunnerStatus, string>? StatusChanged;

    public event Action<PassReport>? PassCompleted;

    /// <summary>Raised when a pass after startup or an offline period transferred many files.</summary>
    public event Action<int>? CatchUpCompleted;

    /// <summary>Raised when a SignalR "Changed" from another device arrives.</summary>
    public event Action<string, string>? RemoteChangeReceived;

    /// <summary>Files changed here or on another device while a pass was running; the next pass syncs them.</summary>
    public int PendingChanges
    {
        get
        {
            lock (_gate)
                return _arrivedDuringPass.Count;
        }
    }

    /// <summary>Raised when <see cref="PendingChanges"/> changes.</summary>
    public event Action<int>? PendingChangesChanged;

    /// <summary>The other computer's big batch this one is waiting for, or null.</summary>
    public PeerWait? WaitingFor
    {
        get
        {
            lock (_gate)
            {
                var first = _peerBatches.OrderBy(b => b.Value.Started).FirstOrDefault();
                return first.Value is null ? null : new PeerWait(first.Key, first.Value.Count, Math.Min(first.Value.Seen, first.Value.Count));
            }
        }
    }

    /// <summary>Raised when waiting for another computer starts, progresses or ends.</summary>
    public event Action<PeerWait?>? PeerWaitChanged;

    /// <summary>"Download now anyway": stop waiting for other computers' batches and sync now.</summary>
    public void ReleaseHold()
    {
        lock (_gate)
            _peerBatches.Clear();
        PeerWaitChanged?.Invoke(null);
        RequestSync("download-now");
    }

    private void OnPeerBatch(string device, int count, bool active)
    {
        if (string.Equals(device, _options.DeviceId, StringComparison.Ordinal) || !_options.WaitForPeerBatches)
            return;
        if (active)
        {
            lock (_gate)
                _peerBatches[device] = new PeerBatchState(count, DateTimeOffset.UtcNow);
            _log.LogInformation("{Device} is uploading {Count} files; waiting to download them in one go", device, count);
            PeerWaitChanged?.Invoke(WaitingFor);
        }
        else
        {
            EndHold(device, "peer-batch-done");
        }
    }

    /// <summary>A Changed event from a computer whose batch we wait for: count it instead of syncing.</summary>
    private bool HoldsRemoteChange(string device)
    {
        lock (_gate)
        {
            if (!_peerBatches.TryGetValue(device, out var batch))
                return false;
            batch.Seen++;
            batch.LastActivity = DateTimeOffset.UtcNow;
        }
        PeerWaitChanged?.Invoke(WaitingFor);
        return true;
    }

    private void EndHold(string device, string reason)
    {
        bool sync;
        lock (_gate)
        {
            if (!_peerBatches.Remove(device))
                return;
            sync = _peerBatches.Count == 0;
        }
        _log.LogInformation("Stopped waiting for {Device} ({Reason})", device, reason);
        PeerWaitChanged?.Invoke(WaitingFor);
        if (sync)
            RequestSync(reason, full: true);
    }

    private void CheckHolds()
    {
        var now = DateTimeOffset.UtcNow;
        List<(string Device, string Reason)> expired;
        lock (_gate)
        {
            expired = _peerBatches
                .Where(b => now - b.Value.LastActivity > _options.PeerQuietTimeout || now - b.Value.Started > _options.MaxHold)
                .Select(b => (b.Key, now - b.Value.Started > _options.MaxHold ? "peer-batch-too-long" : "peer-batch-quiet"))
                .ToList();
        }
        foreach (var (device, reason) in expired)
            EndHold(device, reason);
    }

    private void ClearHolds()
    {
        lock (_gate)
        {
            if (_peerBatches.Count == 0)
                return;
            _peerBatches.Clear();
        }
        PeerWaitChanged?.Invoke(null);
    }

    /// <summary>Tells the other computers that this pass uploads a big batch (they wait for it).</summary>
    private void OnExecutionStarting(int uploads, int downloads, long bytes)
    {
        if (uploads < _options.BatchThreshold || _hub?.State != HubConnectionState.Connected)
            return;
        lock (_gate)
            _announced = true;
        // Wait for the server to pass the announcement on before the first upload: otherwise the
        // first files can reach the other computer before it knows a batch is coming. Bounded,
        // so a slow push channel never holds up syncing for long.
        InvokeHubAsync("BatchStarted", uploads).Wait(TimeSpan.FromSeconds(3));
    }

    private async Task InvokeHubAsync(string method, params object[] args)
    {
        try
        {
            if (_hub is { State: HubConnectionState.Connected } hub)
                await hub.InvokeCoreAsync(method, args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An older server without the method, or the connection just dropped: harmless.
            _log.LogDebug("Hub call {Method} failed: {Error}", method, ex.Message);
        }
    }

    /// <summary>Remembers a change that arrived while a pass is running, so progress can count it.</summary>
    private void NoteArrival(string path)
    {
        int count;
        lock (_gate)
        {
            if (_passCts is null || !_arrivedDuringPass.Add(path))
                return;
            count = _arrivedDuringPass.Count;
        }
        PendingChangesChanged?.Invoke(count);
    }

    public void Start()
    {
        if (_loop is not null)
            return;
        if (_options.EnableWatcher)
            StartWatcher();
        if (_options.EnableHub)
            _hubLoop = Task.Run(() => ConnectHubAsync(_stop.Token));
        _periodic = new Timer(_ => RequestSync("periodic", full: true), null, _options.PeriodicInterval, _options.PeriodicInterval);
        _holdTimer = new Timer(_ => CheckHolds(), null, _options.HoldCheckInterval, _options.HoldCheckInterval);
        _loop = Task.Run(() => LoopAsync(_stop.Token));
        RequestSync("startup", full: true);
    }

    /// <summary>Requests a pass. Requests are merged; the earliest due time wins.</summary>
    public void RequestSync(string reason, bool full = false, TimeSpan? delay = null)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var due = now + (delay ?? TimeSpan.Zero);
            if (_dueAt is null || due < _dueAt)
                _dueAt = due;
            _fullRequested |= full;
            _pendingReasons.Add(reason);
        }
        _wake.Release();
    }

    /// <summary>Debounced request from the file watcher: waits for quiet, but never longer than the max delay.</summary>
    private void RequestDebounced()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _firstWatcherRequest ??= now;
            var due = now + _options.WatcherDebounce;
            var cap = _firstWatcherRequest.Value + _options.WatcherMaxDelay;
            if (due > cap)
                due = cap;
            // Only push an existing watcher-due time later; never delay other requests.
            if (_dueAt is null || _pendingReasons.SetEquals(["watcher"]))
                _dueAt = due;
            else if (due < _dueAt)
                _dueAt = due;
            _pendingReasons.Add("watcher");
        }
        _wake.Release();
    }

    /// <summary>Call after the computer resumes from sleep: catch up with a full pass.</summary>
    public void NotifyResume()
    {
        lock (_gate)
            _catchUpArmed = true;
        RequestSync("resume", full: true);
    }

    /// <summary>
    /// Pauses syncing now: a pass that is running is cancelled (half-sent files are discarded on
    /// both sides and the next pass picks up where this one stopped), and no new pass starts.
    /// </summary>
    public void Pause()
    {
        CancellationTokenSource? running;
        lock (_gate)
        {
            _paused = true;
            running = _passCts;
        }
        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        SetStatus(RunnerStatus.Paused, "Paused");
    }

    public void Resume()
    {
        lock (_gate)
            _paused = false;
        SetStatus(RunnerStatus.Idle, "Resuming");
        RequestSync("resume", full: true);
    }

    /// <summary>"Allow these deletions": arms the blocked deletions for one pass and runs it now.</summary>
    public int ApproveDeletions()
    {
        var count = _engine.ApproveBlockedDeletions();
        RequestSync("approved-deletions");
        return count;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            lock (_gate)
            {
                wait = _dueAt is null || _paused
                    ? Timeout.InfiniteTimeSpan
                    : _dueAt.Value - DateTimeOffset.UtcNow;
            }

            if (wait > TimeSpan.Zero || wait == Timeout.InfiniteTimeSpan)
            {
                try
                {
                    await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            bool full;
            string[] reasons;
            lock (_gate)
            {
                full = _fullRequested;
                reasons = [.. _pendingReasons];
                _fullRequested = false;
                _pendingReasons.Clear();
                _dueAt = null;
                _firstWatcherRequest = null;
            }

            await RunOnePassAsync(full, reasons, ct).ConfigureAwait(false);
        }
    }

    private async Task RunOnePassAsync(bool full, string[] reasons, CancellationToken ct)
    {
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            if (_paused)
                return;
            _passCts = passCts;
            _arrivedDuringPass.Clear(); // this pass picks them up
        }
        PendingChangesChanged?.Invoke(0);
        SetStatus(RunnerStatus.Syncing, "Syncing");
        bool hold;
        lock (_gate)
            hold = _peerBatches.Count > 0;
        PassResult result;
        try
        {
            result = await _engine.RunPassAsync(new PassOptions(string.Join(",", reasons), full, DeferDownloads: hold), passCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (passCts.IsCancellationRequested)
        {
            // Paused mid-pass. Nothing half-done is kept; the next pass (after Resume) finishes the work.
            _log.LogInformation("Pass stopped because syncing was paused");
            SetStatus(RunnerStatus.Paused, "Paused");
            return;
        }
        catch (Exception ex)
        {
            // Defensive: the engine converts expected failures into results.
            _log.LogError(ex, "Unexpected error in sync pass");
            result = new PassResult { Outcome = PassOutcome.Failed, Message = ex.Message };
        }
        finally
        {
            bool announced;
            lock (_gate)
            {
                _passCts = null;
                announced = _announced;
                _announced = false;
            }
            if (announced)
                _ = InvokeHubAsync("BatchFinished");
        }

        LastResult = result;
        var now = DateTimeOffset.UtcNow;
        switch (result.Outcome)
        {
            case PassOutcome.Completed:
                LastSyncAt = now;
                _offlineAttempts = 0;
                bool announce;
                lock (_gate)
                {
                    announce = _catchUpArmed && result.Changes >= _options.CatchUpThreshold;
                    _catchUpArmed = false;
                }
                if (announce)
                    CatchUpCompleted?.Invoke(result.Changes);
                if (result.Errors > 0 || result.Warnings > 0)
                    SetStatus(RunnerStatus.Error, $"Synced with {result.Errors} error(s), {result.Warnings} warning(s)");
                else
                    SetStatus(RunnerStatus.Idle, "Up to date");
                if (result.Unstable > 0 || result.Deferred > 0)
                    RequestSync("retry-unstable", delay: _options.UnstableRetry);
                else if (result.Errors > 0)
                    RequestSync("retry-errors", delay: _options.ErrorRetry);
                break;
            case PassOutcome.Offline:
                lock (_gate)
                    _catchUpArmed = true;
                var backoff = _options.OfflineBackoff[Math.Min(_offlineAttempts, _options.OfflineBackoff.Count - 1)];
                _offlineAttempts++;
                SetStatus(RunnerStatus.Offline, "Offline: " + result.Message);
                RequestSync("retry-offline", full: true, delay: backoff);
                break;
            case PassOutcome.AuthFailed:
                SetStatus(RunnerStatus.Error, "The server rejected the token");
                RequestSync("retry-auth", delay: _options.AuthRetry);
                break;
            case PassOutcome.Blocked:
                SetStatus(RunnerStatus.Blocked, result.Message ?? result.BlockReason.ToString());
                break;
            default:
                SetStatus(RunnerStatus.Error, "Sync failed: " + result.Message);
                RequestSync("retry-failed", delay: _options.ErrorRetry);
                break;
        }

        lock (_gate)
        {
            if (_paused)
                SetStatus(RunnerStatus.Paused, "Paused");
        }
        PassCompleted?.Invoke(new PassReport(result, reasons, now));
    }

    private void SetStatus(RunnerStatus status, string text)
    {
        Status = status;
        StatusText = text;
        try
        {
            StatusChanged?.Invoke(status, text);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Status handler failed: {Error}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ file watcher

    private void StartWatcher()
    {
        try
        {
            _watcher?.Dispose();
            var w = new FileSystemWatcher(_engine.Root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += OnFsEvent;
            w.Changed += OnFsEvent;
            w.Deleted += OnFsEvent;
            w.Renamed += (s, e) =>
            {
                OnFsEvent(s, new FileSystemEventArgs(WatcherChangeTypes.Deleted, _engine.Root, e.OldName));
                OnFsEvent(s, e);
            };
            w.Error += (_, e) =>
            {
                _log.LogWarning("File watcher error ({Error}); scheduling a full pass", e.GetException().Message);
                RequestSync("watcher-overflow", full: true);
                if (!Directory.Exists(_engine.Root))
                    return;
                _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => { if (!_stop.IsCancellationRequested) StartWatcher(); }, TaskScheduler.Default);
            };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
        {
            // Folder missing: the periodic pass reports it; retry the watcher later.
            _log.LogWarning("Cannot watch {Folder}: {Error}", _engine.Root, ex.Message);
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => { if (!_stop.IsCancellationRequested) StartWatcher(); }, TaskScheduler.Default);
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Name))
            return;
        var rel = PathRules.FromOsRelative(e.Name);
        if (rel.Equals(PathRules.TempFolderName, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(PathRules.TempFolderName + "/", StringComparison.OrdinalIgnoreCase)
            || rel.Equals(PathRules.MarkerFileName, StringComparison.OrdinalIgnoreCase))
            return;
        // Ignored files and anything inside ignored folders never trigger a pass
        // (the periodic full pass still covers rare misclassifications).
        if (_engine.Ignore.IsIgnored(rel) || _engine.Ignore.IsIgnoredDirectory(rel))
            return;
        if (_engine.WasRecentlyTouched(rel, OwnEventWindow))
            return;
        NoteArrival(rel);
        RequestDebounced();
    }

    // ------------------------------------------------------------------ SignalR

    private async Task ConnectHubAsync(CancellationToken ct)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Api.TetherApiClient.NormalizeBase(_options.ServerUrl), "hub"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(_options.Token);
                o.Headers[TetherHeaders.DeviceId] = Uri.EscapeDataString(_options.DeviceId);
                o.Headers[TetherHeaders.App] = TetherInfo.AppDescription;
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _hub.On<string, string>("Changed", (device, path) =>
        {
            RemoteChangeReceived?.Invoke(device, path);
            if (string.Equals(device, _options.DeviceId, StringComparison.Ordinal))
                return; // our own change echoed back
            if (HoldsRemoteChange(device))
                return; // part of a big batch: downloaded in one go when it is complete
            NoteArrival(path);
            RequestSync("remote-change", delay: _options.RemoteDebounce);
        });
        _hub.On<string, int, bool>("PeerBatch", OnPeerBatch);
        _hub.Reconnecting += _ =>
        {
            ClearHolds(); // the server re-sends active batches when we are back
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            _log.LogInformation("Push channel reconnected");
            RequestSync("hub-reconnected");
            return Task.CompletedTask;
        };
        _hub.Closed += async _ =>
        {
            if (!ct.IsCancellationRequested)
                await StartHubWithRetryAsync(ct).ConfigureAwait(false);
        };

        await StartHubWithRetryAsync(ct).ConfigureAwait(false);
    }

    private async Task StartHubWithRetryAsync(CancellationToken ct)
    {
        var policy = new ForeverRetryPolicy();
        for (var attempt = 0; !ct.IsCancellationRequested && _hub is not null; attempt++)
        {
            try
            {
                await _hub.StartAsync(ct).ConfigureAwait(false);
                _log.LogInformation("Push channel connected");
                if (attempt > 0)
                    RequestSync("hub-connected");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogDebug("Push channel connect failed: {Error}", ex.Message);
                var delay = policy.NextRetryDelay(new RetryContext { PreviousRetryCount = attempt }) ?? TimeSpan.FromSeconds(30);
                try
                {
                    await Task.Delay(delay == TimeSpan.Zero ? TimeSpan.FromMilliseconds(200) : delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Reconnect forever: 0, 2, 5, 10, then every 30 seconds.</summary>
    public sealed class ForeverRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            Delays[(int)Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1)];
    }

    // ------------------------------------------------------------------ shutdown

    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested)
            return;
        _stop.Cancel();
        _periodic?.Dispose();
        _holdTimer?.Dispose();
        _watcher?.Dispose();
        if (_hub is not null)
        {
            try
            {
                await _hub.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug("Hub dispose: {Error}", ex.Message);
            }
        }
        foreach (var t in new[] { _loop, _hubLoop })
        {
            if (t is null)
                continue;
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _wake.Dispose();
    }
}
