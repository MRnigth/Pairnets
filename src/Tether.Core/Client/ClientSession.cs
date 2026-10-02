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
        string? stateBaseDir = null, Func<RunnerOptions, RunnerOptions>? runnerOptions = null, TimeProvider? clock = null)
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
        var engine = new SyncEngine(new EngineOptions
        {
            Folder = folder,
            DeviceName = settings.DeviceName!,
            ExtraIgnore = settings.ExtraIgnore,
        }, api, state, trash, loggers.CreateLogger("Tether.Engine"), clock);
        var options = new RunnerOptions { ServerUrl = url, Token = token, DeviceId = settings.DeviceName! };
        if (runnerOptions is not null)
            options = runnerOptions(options);
        var runner = new SyncRunner(engine, options, loggers.CreateLogger("Tether.Runner"));

        var session = new ClientSession(settings, state, api, engine, runner, clock);
        session.Wire();
        runner.Start();
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
        Engine.Progress += p => Update(s => p.CurrentPath is null
            ? s with { CurrentPath = null, Operation = null, BytesDone = 0, BytesTotal = 0, FilesDone = p.FilesDone, FilesTotal = p.FilesTotal }
            : s with { CurrentPath = p.CurrentPath, Operation = p.Operation, BytesDone = p.BytesDone, BytesTotal = p.BytesTotal, FilesDone = p.FilesDone, FilesTotal = p.FilesTotal });
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

    public async ValueTask DisposeAsync()
    {
        await Runner.DisposeAsync().ConfigureAwait(false);
        Api.Dispose();
        State.Dispose();
    }
}
