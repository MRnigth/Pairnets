using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Core.Api;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.State;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>
/// One running sync session for the desktop apps (Windows and macOS/Linux): state database, API
/// client, engine and runner, plus an activity feed and a status snapshot for the UI. All UI
/// frameworks share this class so the apps only draw and forward clicks.
/// </summary>
public sealed class ClientSession : IAsyncDisposable, IHistorySource
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
    private Timer? _devicesTimer;

    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ServerInfoInterval = TimeSpan.FromMinutes(5);

    /// <summary>How often the list of computers (online or not) is re-read.</summary>
    private static readonly TimeSpan DevicesInterval = TimeSpan.FromSeconds(30);

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
            await RefreshDevicesAsync().ConfigureAwait(false);
            await CheckAccountAsync(info).ConfigureAwait(false);
            return info;
        }
        catch (PairnetsAuthException ex) when (ex.NeedsSignIn)
        {
            NoticeSignedOut();
            return null;
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the computers that use the server and which are online (the Devices page and the
    /// overview's picture). Failures are ignored; offline is shown elsewhere.
    /// </summary>
    public async Task RefreshDevicesAsync()
    {
        try
        {
            var devices = await Api.GetDevicesAsync(CancellationToken.None).ConfigureAwait(false);
            Update(s => s with { Devices = devices ?? [], DevicesUnsupported = devices is null });
        }
        catch (PairnetsAuthException ex) when (ex.NeedsSignIn)
        {
            NoticeSignedOut();
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ this computer's key and name

    /// <summary>
    /// Raised when the app should save these settings (with the given token or key, protected) and start a new
    /// session: this computer got its own key in place of the shared token, moved to the nest's HTTPS name, or
    /// was renamed on the nest.
    /// </summary>
    public event Action<ClientSettings, string>? AccountChanged;

    /// <summary>Raised when another computer asks to join the nest (for a notification).</summary>
    public event Action<JoinRequest>? JoinRequested;

    /// <summary>How long a join request is shown: as long as its code works.</summary>
    private static readonly TimeSpan JoinRequestLifetime = TimeSpan.FromMinutes(10);

    private int _accountCheckRunning;
    private bool _moveTried;

    /// <summary>
    /// Brings this computer's sign-in up to date with what the nest offers. Silent and safe to repeat:
    /// <list type="bullet">
    /// <item>On the shared token, when the nest signs computers in, syncing stops until a person signs this one in.</item>
    /// <item>With its own key, it adopts a name changed on the nest.</item>
    /// <item>When the nest has an HTTPS name, it moves there, but only if that address is the same server.</item>
    /// </list>
    /// </summary>
    public async Task CheckAccountAsync(ServerInfo info, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _accountCheckRunning, 1) == 1)
            return;
        try
        {
            var hello = await Api.GetHelloAsync(ct).ConfigureAwait(false);
            if (hello is null)
                return; // a server from before per-computer keys
            var nestUrl = hello.SignIn ? Nest.SafeOrigin(hello.PublicUrl) : null; // opened in the browser: see Nest.SafeOrigin
            if (hello.SignIn && nestUrl != Status.NestUrl)
                Update(s => s with { NestUrl = nestUrl });
            var next = Settings.Clone();
            var token = _token;
            var changed = false;
            if (!Settings.HasOwnKey && hello.SignIn)
            {
                // No more silent key here: a person approves every computer on the nest's website.
                RequireSignIn();
            }
            else if (Settings.HasOwnKey && await Api.GetMeAsync(ct).ConfigureAwait(false) is { } me
                && me.Id == Settings.DeviceId && !string.Equals(me.Name, Settings.DeviceName, StringComparison.Ordinal))
            {
                (next.DeviceName, changed) = (me.Name, true);
                Activity.Add(ActivityKind.Info, null, $"This computer is now called {me.Name}", _clock);
            }
            if (!_moveTried && PairnetsApiClient.TryParseServerUrl(hello.PublicUrl, out var publicUrl) && publicUrl is not null
                && !SameAddress(publicUrl, Settings.ServerUrl))
            {
                _moveTried = true;
                if (await IsSameServerAsync(publicUrl, token, next.DeviceName!, info.ServerId, ct).ConfigureAwait(false))
                {
                    (next.ServerUrl, changed) = (publicUrl.ToString(), true);
                    _log.LogInformation("Moving to the nest's own address {Url}", publicUrl);
                }
            }
            if (changed)
                AccountChanged?.Invoke(next, token);
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or ObjectDisposedException or OperationCanceledException)
        {
            _log.LogDebug("Account check skipped: {Error}", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _accountCheckRunning, 0);
        }
    }

    private static async Task<bool> IsSameServerAsync(Uri url, string token, string deviceName, string serverId, CancellationToken ct)
    {
        try
        {
            using var probe = new PairnetsApiClient(url, token, deviceName);
            return (await probe.GetInfoAsync(ct).ConfigureAwait(false)).ServerId == serverId;
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            return false; // e.g. the name does not resolve on this network: stay on the address that works
        }
    }

    private static bool SameAddress(Uri a, string? b) =>
        PairnetsApiClient.TryParseServerUrl(b, out var other) && other is not null
        && Uri.Compare(a, other, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The nest no longer lets this computer in: a pass finds out why and stops with "Sign in again".</summary>
    private void NoticeSignedOut()
    {
        if (Status.BlockReason != BlockReason.SignedOut)
            Runner.RequestSync("signed-out");
    }

    /// <summary>
    /// Still on the shared token although the nest signs computers in: syncing stops until a person signs
    /// this computer in with the browser. Its folder, files and settings all stay as they are.
    /// </summary>
    private void RequireSignIn()
    {
        if (Engine.SignInRequired)
            return;
        Engine.SignInRequired = true;
        _log.LogInformation("This nest signs computers in; syncing stops until this computer is signed in");
        Activity.Add(ActivityKind.Info, null, "Sign in to your nest to keep syncing", _clock);
        Runner.RequestSync("sign-in-required");
    }

    /// <summary>
    /// "Sign out of this computer": removes this computer's key on the nest. Returns false when it has no key of its
    /// own (shared token) or the nest no longer knew it. The app then stops the session and forgets the key.
    /// </summary>
    public async Task<bool> SignOutAsync(CancellationToken ct = default) =>
        Settings.HasOwnKey && await Api.RemoveDeviceAsync(Settings.DeviceId!, ct).ConfigureAwait(false);

    private readonly ILogger _log;
    private readonly string _token;

    private ClientSession(ClientSettings settings, string token, StateDb state, PairnetsApiClient api, SyncEngine engine, SyncRunner runner, TimeProvider clock, ILogger log)
    {
        _log = log;
        _token = token;
        Settings = settings;
        State = state;
        Api = api;
        Engine = engine;
        Runner = runner;
        _clock = clock;
    }

    public ClientSettings Settings { get; }
    public StateDb State { get; }
    public PairnetsApiClient Api { get; }
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
        var stateDir = PairnetsPaths.StateDirFor(folder, stateBaseDir);
        var state = new StateDb(Path.Combine(stateDir, "state.db"));
        state.SetMeta(StateDb.MetaFolder, Path.GetFullPath(folder));
        var url = new Uri(settings.ServerUrl!);
        var api = new PairnetsApiClient(url, token, settings.DeviceName!);
        api.UploadLimit.SetMegabytesPerSecond(settings.UploadLimitMBps);
        api.DownloadLimit.SetMegabytesPerSecond(settings.DownloadLimitMBps);
        var engine = new SyncEngine(new EngineOptions
        {
            Folder = folder,
            DeviceName = settings.DeviceName!,
            ExtraIgnore = settings.ExtraIgnore,
            MaxParallelTransfers = settings.EffectiveParallelTransfers,
        }, api, state, trash, loggers.CreateLogger("Pairnets.Engine"), clock, hooks);
        var options = new RunnerOptions { ServerUrl = url, Token = token, DeviceId = settings.DeviceName!, WaitForPeerBatches = settings.WaitForPeerBatches };
        if (runnerOptions is not null)
            options = runnerOptions(options);
        var runner = new SyncRunner(engine, options, loggers.CreateLogger("Pairnets.Runner"));

        var session = new ClientSession(settings, token, state, api, engine, runner, clock, loggers.CreateLogger("Pairnets.Session"));
        session.Wire();
        session._status = session._status with { LimitText = settings.LimitText };
        runner.Start();
        session._infoTimer = new Timer(_ => _ = session.RefreshServerInfoAsync(), null, TimeSpan.Zero, ServerInfoInterval);
        session._devicesTimer = new Timer(_ => _ = session.RefreshDevicesAsync(), null, TimeSpan.Zero, DevicesInterval);
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
        Runner.JoinRequested += (code, name, system) =>
        {
            var request = new JoinRequest(code, name, system, _clock.GetUtcNow());
            Update(s => s with { JoinRequests = [request, .. LiveJoinRequests(s).Where(r => r.Code != code)] });
            JoinRequested?.Invoke(request);
        };
        Runner.JoinDecided += code => Update(s => s with { JoinRequests = LiveJoinRequests(s).Where(r => r.Code != code).ToList() });
        Runner.DeviceListChanged += (id, name, removed) =>
        {
            if (id == Settings.DeviceId && removed)
                Runner.RequestSync("this-computer-removed"); // the pass learns why and stops with "Sign in again"
            else if (id == Settings.DeviceId && Status.Server is { } info)
                _ = CheckAccountAsync(info);
            _ = RefreshDevicesAsync();
        };
        Runner.RemoteChangeReceived += (device, _) =>
        {
            if (string.Equals(device, Settings.DeviceName, StringComparison.OrdinalIgnoreCase))
                return; // our own change echoed back
            var at = _clock.GetUtcNow();
            Update(s => s with { HeardFrom = new Dictionary<string, DateTimeOffset>(s.HeardFrom, StringComparer.OrdinalIgnoreCase) { [device] = at } });
        };
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

    /// <summary>The join requests whose code still works.</summary>
    private IEnumerable<JoinRequest> LiveJoinRequests(StatusSnapshot s)
    {
        var now = _clock.GetUtcNow();
        return s.JoinRequests.Where(r => now - r.At < JoinRequestLifetime);
    }

    /// <summary>Forgets join requests that have expired (the window calls this when it redraws).</summary>
    public void PruneJoinRequests()
    {
        var status = Status;
        if (status.JoinRequests.Count > 0 && LiveJoinRequests(status).Count() != status.JoinRequests.Count)
            Update(s => s with { JoinRequests = LiveJoinRequests(s).ToList() });
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
        sb.AppendLine($"Pairnets stopped because this sync would delete {pending.Count} file(s):");
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
            return "That folder is not your Pairnets folder (its .pairnets-marker is missing or different). Nothing was changed.";
        return null;
    }

    /// <summary>True when the server runs an older release than this app and the user did not skip that version.</summary>
    public bool ServerNeedsUpdate(ServerInfo? info = null)
    {
        info ??= Status.Server;
        return info?.ServerVersion is { } v && UpdateChecker.ServerIsOlder(v, PairnetsInfo.ProductVersion)
            && !string.Equals(v, Settings.SkippedServerVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Update server": asks the server to update itself, then follows it (it restarts on the way)
    /// until it reports a newer version, the updater reports a result, or <paramref name="timeout"/> passes.
    /// Every step is logged (Debug mode shows the polls too) and sent to <paramref name="trace"/>; a failure,
    /// or any run with a trace, also carries what the server knows about its updater in <see cref="ServerUpdateResult.Details"/>.
    /// </summary>
    public async Task<ServerUpdateResult> UpdateServerAsync(IProgress<string>? progress, CancellationToken ct,
        TimeSpan? pollInterval = null, TimeSpan? timeout = null, IProgress<string>? trace = null)
    {
        var started = _clock.GetUtcNow();
        void Trace(string line, LogLevel level)
        {
            _log.Log(level, "Server update: {Step}", line);
            trace?.Report($"[{(_clock.GetUtcNow() - started).TotalSeconds,3:0}s] {line}");
        }

        ServerUpdateResult result;
        try
        {
            result = await RunServerUpdateAsync(progress, Trace, ct, pollInterval, timeout).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Saving Settings restarts the session; a window still holding the old one ends up here.
            result = new ServerUpdateResult(false, "The connection to the server was restarted (settings were saved). Try again.",
                Status.Server?.ServerVersion, CanUpdateItself: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Server update failed unexpectedly");
            result = new ServerUpdateResult(false, $"Unexpected error: {ex.GetType().Name}: {ex.Message}",
                Status.Server?.ServerVersion, CanUpdateItself: true);
        }
        Trace((result.Success ? "Done: " : "Not updated: ") + result.Message, result.Success ? LogLevel.Information : LogLevel.Warning);
        if (result.Success && trace is null)
            return result;
        var details = await GetUpdaterDiagnosticsTextAsync(ct).ConfigureAwait(false);
        _log.Log(result.Success ? LogLevel.Debug : LogLevel.Warning, "Server updater diagnostics:{NewLine}{Details}", Environment.NewLine, details);
        return result with { Details = details };
    }

    /// <summary>What the server knows about its self-updater, as text (never throws for network problems).</summary>
    public async Task<string> GetUpdaterDiagnosticsTextAsync(CancellationToken ct)
    {
        try
        {
            var diagnostics = await Api.GetUpdateDiagnosticsAsync(ct).ConfigureAwait(false);
            return diagnostics?.ToReport()
                ?? "This server is too old to report updater details. Update it once by hand: curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Could not read the server's updater details: " + ex.Message;
        }
    }

    private async Task<ServerUpdateResult> RunServerUpdateAsync(IProgress<string>? progress, Action<string, LogLevel> trace,
        CancellationToken ct, TimeSpan? pollInterval, TimeSpan? timeout)
    {
        var before = Status.Server?.ServerVersion;
        trace($"Asking the server to update (it runs {before ?? "an unknown version"}, this app is {PairnetsInfo.ProductVersion})", LogLevel.Information);
        PairnetsApiClient.ServerUpdateRequest request;
        try
        {
            request = await Api.RequestServerUpdateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            return new ServerUpdateResult(false, "Could not reach the server: " + ex.Message, before, CanUpdateItself: true);
        }
        trace("The server answered the request: " + request switch
        {
            PairnetsApiClient.ServerUpdateRequest.Requested => "accepted, the updater should start now",
            PairnetsApiClient.ServerUpdateRequest.TooSoon => "too soon after the last request",
            PairnetsApiClient.ServerUpdateRequest.UpdaterMissing => "this server has no self-updater",
            _ => "this server does not know about updates",
        }, LogLevel.Information);
        switch (request)
        {
            case PairnetsApiClient.ServerUpdateRequest.UpdaterMissing or PairnetsApiClient.ServerUpdateRequest.NotSupported:
                return new ServerUpdateResult(false, "This server can't update itself yet. Run the install command on the server once.", before, CanUpdateItself: false);
            case PairnetsApiClient.ServerUpdateRequest.TooSoon:
                // A click a moment ago already asked; follow that request if it is still under way.
                ServerInfo? pending = null;
                try
                {
                    pending = await Api.GetInfoAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException)
                {
                }
                switch (pending?.Updater?.State)
                {
                    case "succeeded":
                        // The last click's update already finished: show its result instead of "too soon".
                        trace("The update asked for a moment ago has already finished", LogLevel.Information);
                        return new ServerUpdateResult(true, pending.Updater.Message ?? "Done.", pending.ServerVersion, CanUpdateItself: true,
                            AlreadyUpToDate: !UpdateChecker.IsNewer(pending.ServerVersion, before));
                    case "failed":
                        return new ServerUpdateResult(false, pending.Updater.Message ?? "The update failed. Nothing was changed.", before, CanUpdateItself: true);
                    case not ("requested" or "running"):
                        return new ServerUpdateResult(false, "An update was just requested. Wait a minute and try again.", before, CanUpdateItself: true);
                }
                trace("An update asked for a moment ago is still under way; following it", LogLevel.Information);
                break;
        }

        progress?.Report("Downloading and checking the new version");
        var deadline = _clock.GetUtcNow() + (timeout ?? TimeSpan.FromMinutes(3));
        var restarting = false;
        string? lastSeen = null;
        while (_clock.GetUtcNow() < deadline)
        {
            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(3), _clock, ct).ConfigureAwait(false);
            ServerInfo info;
            try
            {
                info = await Api.GetInfoAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException)
            {
                if (!restarting)
                {
                    progress?.Report("Installing and restarting the server");
                    trace("The server stopped answering (restarting?): " + ex.Message, LogLevel.Information);
                }
                restarting = true;
                continue;
            }
            var seen = $"server {info.ServerVersion ?? "?"}, updater {info.Updater?.State ?? "?"}{(info.Updater?.Message is { } m ? ": " + m : string.Empty)}";
            if (seen != lastSeen)
                trace(seen, LogLevel.Debug);
            lastSeen = seen;
            restarting = false;
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
                    // Succeeded without a newer version: the newest release is the one the server already runs.
                    return new ServerUpdateResult(true, info.Updater.Message ?? "Done.", info.ServerVersion, CanUpdateItself: true,
                        AlreadyUpToDate: info.ServerVersion == before);
                case "running":
                    progress?.Report(info.Updater.Message ?? "Installing");
                    break;
            }
        }
        return new ServerUpdateResult(false, "The server did not finish updating in time. Turn on Debug mode in Settings and try again to see the server's update log.", before, CanUpdateItself: true);
    }

    /// <summary>
    /// The "update the server automatically" path: updates without asking and notes the outcome
    /// in the activity list. Returns the result so the app can still help with a one-time setup.
    /// </summary>
    public async Task<ServerUpdateResult> UpdateServerQuietlyAsync(CancellationToken ct)
    {
        var from = Status.Server?.ServerVersion;
        Activity.Add(ActivityKind.Info, null, $"Updating the server from {from}…", _clock);
        var result = await UpdateServerAsync(null, ct).ConfigureAwait(false);
        Activity.Add(result.Success ? ActivityKind.Info : ActivityKind.Warning, null,
            result.AlreadyUpToDate ? $"The server is already up to date ({result.ServerVersion})"
            : result.Success ? $"Server updated to {result.ServerVersion}"
            : "Server not updated: " + result.Message, _clock);
        return result;
    }

    /// <summary>"Download now anyway": stop waiting for another computer's big batch.</summary>
    public void DownloadNow() => Runner.ReleaseHold();

    // ------------------------------------------------------------------ history (the History page)

    /// <summary>Every file the server knows, deleted ones included, read in one request.</summary>
    public async Task<IReadOnlyList<ServerFile>> GetServerFilesAsync(CancellationToken ct)
    {
        var manifest = await Api.GetManifestAsync(null, ct).ConfigureAwait(false);
        return manifest.Entries.Select(ServerFile.From).ToList();
    }

    /// <summary>The older versions of <paramref name="path"/> the server keeps, newest first.</summary>
    public async Task<IReadOnlyList<HistoryVersion>> GetVersionsAsync(string path, CancellationToken ct) =>
        (await Api.GetHistoryAsync(path, ct).ConfigureAwait(false)).OrderByDescending(v => v.StoredAtUtc).ToList();

    /// <summary>
    /// Makes a stored version the current one again (the version it replaces is kept in history
    /// too). The server tells the other computers; this one fetches it straight away, because it
    /// ignores the server's announcement of its own requests. Returns null on success, or what went wrong.
    /// </summary>
    public async Task<string?> RestoreVersionAsync(string path, HistoryVersion version, CancellationToken ct)
    {
        ApiResult result;
        try
        {
            result = await Api.RestoreAsync(path, version.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            return "Could not reach the server: " + ex.Message;
        }
        if (result.Outcome != ApiOutcome.Ok)
            return result.Outcome == ApiOutcome.NotFound
                ? "That version is no longer on the server (it was older than the server keeps)."
                : "The server did not restore it: " + (result.Message ?? result.Outcome.ToString());
        Activity.Add(ActivityKind.Info, path, $"Restored {PathRules.FileName(path)} (the version from {Format.Moment(version.StoredAtUtc, _clock.GetUtcNow())})", _clock);
        Runner.RequestSync("restored", full: true);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_infoTimer is not null)
            await _infoTimer.DisposeAsync().ConfigureAwait(false);
        if (_devicesTimer is not null)
            await _devicesTimer.DisposeAsync().ConfigureAwait(false);
        await Runner.DisposeAsync().ConfigureAwait(false);
        Api.Dispose();
        State.Dispose();
    }
}

/// <summary>
/// How "Update server" ended. <see cref="CanUpdateItself"/> false: show the one-time install command.
/// <see cref="Details"/> is the server's updater report (failures, and Debug mode).
/// </summary>
public sealed record ServerUpdateResult(bool Success, string Message, string? ServerVersion, bool CanUpdateItself,
    bool AlreadyUpToDate = false, string? Details = null);
