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
}

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
        PassResult result;
        try
        {
            result = await _engine.RunPassAsync(new PassOptions(string.Join(",", reasons), full), passCts.Token).ConfigureAwait(false);
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
            lock (_gate)
                _passCts = null;
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
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _hub.On<string, string>("Changed", (device, path) =>
        {
            RemoteChangeReceived?.Invoke(device, path);
            if (string.Equals(device, _options.DeviceId, StringComparison.Ordinal))
                return; // our own change echoed back
            NoteArrival(path);
            RequestSync("remote-change", delay: _options.RemoteDebounce);
        });
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
