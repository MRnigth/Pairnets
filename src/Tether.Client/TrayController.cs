using System.Diagnostics;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Tether.Client.Platform;
using Tether.Client.Ui;
using Tether.Core.Api;
using Tether.Core.Logging;
using Tether.Core.Paths;
using Tether.Core.Settings;
using Tether.Core.State;
using Tether.Core.Sync;
using Forms = System.Windows.Forms;

namespace Tether.Client;

/// <summary>
/// Owns the tray icon, menu, notifications and the sync session (engine + runner). Everything that
/// decides what to sync lives in Tether.Core; this class only reflects state and forwards clicks.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly App _app;
    private readonly ILoggerFactory _loggers;
    private readonly RollingFileLoggerProvider _fileLog;
    private readonly ILogger _log;
    private readonly DpapiProtector _protector = new();
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<string, DateTime> _lastToast = [];
    private ClientSettings _settings;
    private Session? _session;
    private Action? _balloonClick;
    private long _lastProgressTicks;

    private readonly Forms.ToolStripMenuItem _statusItem = new("Starting…") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _syncNow = new("Sync now");
    private readonly Forms.ToolStripMenuItem _openFolder = new("Open folder");
    private readonly Forms.ToolStripMenuItem _allowDeletes = new("Allow these deletions…") { Visible = false };
    private readonly Forms.ToolStripMenuItem _fixProblem = new("Fix…") { Visible = false };
    private readonly Forms.ToolStripMenuItem _warnings = new("Files needing attention…") { Visible = false };
    private readonly Forms.ToolStripMenuItem _settingsItem = new("Settings…");
    private readonly Forms.ToolStripMenuItem _viewLog = new("View log");
    private readonly Forms.ToolStripMenuItem _pause = new("Pause syncing");
    private readonly Forms.ToolStripMenuItem _autoStart = new("Start with Windows") { CheckOnClick = true };
    private readonly Forms.ToolStripMenuItem _exit = new("Exit");

    public TrayController(App app, ILoggerFactory loggers, RollingFileLoggerProvider fileLog)
    {
        _app = app;
        _loggers = loggers;
        _fileLog = fileLog;
        _log = loggers.CreateLogger("Tether.Tray");
        _settings = SettingsStore.Load(SettingsStore.DefaultPath);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([_statusItem, new Forms.ToolStripSeparator(), _syncNow, _openFolder, _allowDeletes, _fixProblem, _warnings,
            new Forms.ToolStripSeparator(), _settingsItem, _viewLog, _pause, _autoStart, new Forms.ToolStripSeparator(), _exit]);
        _syncNow.Click += (_, _) => _session?.Runner.RequestSync("manual", full: true);
        _openFolder.Click += (_, _) => OpenFolder();
        _allowDeletes.Click += (_, _) => ConfirmDeletions();
        _fixProblem.Click += (_, _) => FixBlocked();
        _warnings.Click += (_, _) => ShowWarnings();
        _settingsItem.Click += (_, _) => ShowSettings(firstRun: false);
        _viewLog.Click += (_, _) => Shell(_fileLog.CurrentFile);
        _pause.Click += (_, _) => TogglePause();
        _autoStart.Click += (_, _) => SetAutoStart(_autoStart.Checked);
        _exit.Click += (_, _) => _app.Shutdown();
        menu.Opening += (_, _) => _autoStart.Checked = SafeIsAutoStart();

        _icon = new Forms.NotifyIcon
        {
            Icon = TrayIcons.For(RunnerStatus.Offline),
            Text = "Tether",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenFolder();
        _icon.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Start()
    {
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            UpdateStatus(RunnerStatus.Offline, "Not set up yet");
            ShowSettings(firstRun: true);
            return;
        }
        StartSession(null);
    }

    // ------------------------------------------------------------------ session

    private sealed record Session(StateDb State, TetherApiClient Api, SyncEngine Engine, SyncRunner Runner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Runner.DisposeAsync();
            Api.Dispose();
            State.Dispose();
        }
    }

    private void StartSession(string? plainToken)
    {
        StopSession();
        try
        {
            plainToken ??= _protector.Unprotect(_settings.ProtectedToken!);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cannot decrypt the saved token: {Error}", ex.Message);
            Toast("token", "Tether needs the token again", "The saved token cannot be read on this Windows account. Open Settings and enter it.", Forms.ToolTipIcon.Warning, () => ShowSettings(false));
            UpdateStatus(RunnerStatus.Error, "Token missing");
            return;
        }

        var folder = _settings.Folder!;
        var stateDir = TetherPaths.StateDirFor(folder);
        var state = new StateDb(Path.Combine(stateDir, "state.db"));
        state.SetMeta(StateDb.MetaFolder, Path.GetFullPath(folder));
        var api = new TetherApiClient(new Uri(_settings.ServerUrl!), plainToken, _settings.DeviceName!);
        var engine = new SyncEngine(new EngineOptions
        {
            Folder = folder,
            DeviceName = _settings.DeviceName!,
            ExtraIgnore = _settings.ExtraIgnore,
        }, api, state, new RecycleBinTrash(_loggers.CreateLogger("Tether.Trash")), _loggers.CreateLogger("Tether.Engine"));
        var runner = new SyncRunner(engine, new RunnerOptions
        {
            ServerUrl = new Uri(_settings.ServerUrl!),
            Token = plainToken,
            DeviceId = _settings.DeviceName!,
        }, _loggers.CreateLogger("Tether.Runner"));

        engine.ConflictCreated += c => _app.RunOnUi(() => Toast("conflict:" + c.Path, "Conflict: both PCs changed a file",
            $"{c.Path} was changed on both PCs. Your version was kept as \"{PathRules.FileName(c.ConflictCopyPath)}\".", Forms.ToolTipIcon.Warning, OpenFolder));
        engine.PathWarningRaised += w => _app.RunOnUi(() => Toast("warning:" + w.Path, w.Code == ErrorCodes.CaseCollision ? "Name collision" : "File name not allowed",
            $"{w.Path}: {w.Message}", Forms.ToolTipIcon.Warning, ShowWarnings));
        runner.StatusChanged += (status, text) => _app.RunOnUi(() => UpdateStatus(status, text));
        engine.Progress += p =>
        {
            // At most a few updates per second: progress fires for every transferred chunk.
            var now = Environment.TickCount64;
            if (p.CurrentPath is null || now - Interlocked.Read(ref _lastProgressTicks) < 500)
                return;
            Interlocked.Exchange(ref _lastProgressTicks, now);
            var percent = p.BytesTotal > 0 ? $" {p.BytesDone * 100 / p.BytesTotal}%" : string.Empty;
            var text = $"Syncing {p.FilesDone + 1}/{p.FilesTotal}: {PathRules.FileName(p.CurrentPath)}{percent}";
            _app.RunOnUi(() => UpdateStatus(RunnerStatus.Syncing, text));
        };
        runner.PassCompleted += report => _app.RunOnUi(() => OnPassCompleted(report.Result));
        runner.CatchUpCompleted += n => _app.RunOnUi(() => Toast("catchup", "Tether is up to date", $"Synced {n} file change(s) made while this PC was away.", Forms.ToolTipIcon.Info, null));

        _session = new Session(state, api, engine, runner);
        runner.Start();
        if (_settings.Paused)
            runner.Pause();
        _pause.Text = _settings.Paused ? "Resume syncing" : "Pause syncing";
        _log.LogInformation("Syncing {Folder} with {Server} as {Device}", folder, _settings.ServerUrl, _settings.DeviceName);
    }

    private void StopSession()
    {
        if (_session is null)
            return;
        var s = _session;
        _session = null;
        // Let the current pass finish its file; never block the UI thread for long.
        Task.Run(async () => await s.DisposeAsync()).Wait(TimeSpan.FromSeconds(30));
    }

    // ------------------------------------------------------------------ status

    private void UpdateStatus(RunnerStatus status, string text)
    {
        _icon.Icon = TrayIcons.For(status);
        var when = _session?.Runner.LastSyncAt is { } at ? $" (last sync {at.ToLocalTime():HH:mm})" : string.Empty;
        var tip = $"Tether: {text}{when}";
        _icon.Text = tip.Length > 127 ? tip[..124] + "..." : tip;
        _statusItem.Text = text.Length > 80 ? text[..77] + "..." : text;
    }

    private void OnPassCompleted(PassResult result)
    {
        var blocked = result.Outcome == PassOutcome.Blocked;
        var deletions = blocked && result.BlockReason is BlockReason.MassDelete or BlockReason.FolderEmpty;
        _allowDeletes.Visible = deletions;
        _allowDeletes.Text = $"Allow these deletions ({result.BlockedDeletes.Count})…";
        _fixProblem.Visible = blocked && !deletions;
        _fixProblem.Text = result.BlockReason switch
        {
            BlockReason.FolderMissing or BlockReason.MarkerMissing or BlockReason.MarkerMismatch => "Locate the sync folder…",
            BlockReason.ForeignMarker => "Confirm this folder…",
            BlockReason.ServerChanged or BlockReason.ServerRolledBack => "Re-link to this server…",
            _ => "Fix…",
        };
        _warnings.Visible = _session?.State.LoadWarnings().Count > 0;

        if (deletions)
            Toast("massdelete", "Deletions blocked", result.Message ?? "Many files would be deleted. Nothing was deleted.", Forms.ToolTipIcon.Warning, ConfirmDeletions);
        else if (blocked)
            Toast("blocked:" + result.BlockReason, "Tether paused syncing", result.Message ?? result.BlockReason.ToString(), Forms.ToolTipIcon.Warning, FixBlocked);
        else if (result.Outcome == PassOutcome.AuthFailed)
            Toast("auth", "The server rejected the token", "Open Settings and enter the token printed by install.sh.", Forms.ToolTipIcon.Error, () => ShowSettings(false));
    }

    /// <summary>Balloon tips render as Windows 10/11 toast notifications. Same key at most once per 10 minutes.</summary>
    private void Toast(string key, string title, string text, Forms.ToolTipIcon icon, Action? onClick)
    {
        var now = DateTime.UtcNow;
        if (_lastToast.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10))
            return;
        _lastToast[key] = now;
        _balloonClick = onClick;
        _icon.ShowBalloonTip(8000, title, text, icon);
    }

    // ------------------------------------------------------------------ actions

    private void ConfirmDeletions()
    {
        if (_session is null)
            return;
        var pending = _session.Engine.PendingDeletes;
        if (pending.Count == 0)
            return;
        var sb = new StringBuilder();
        var local = pending.Count(p => p.Side == DeleteSide.Local);
        sb.AppendLine($"Tether stopped because this sync would delete {pending.Count} file(s):");
        sb.AppendLine($"  {local} on this PC (deleted on the other PC)");
        sb.AppendLine($"  {pending.Count - local} on the server (deleted on this PC)");
        sb.AppendLine();
        foreach (var (side, path) in pending.Take(20))
            sb.AppendLine($"  {(side == DeleteSide.Local ? "this PC" : "server")}: {path}");
        if (pending.Count > 20)
            sb.AppendLine($"  … and {pending.Count - 20} more");
        sb.AppendLine();
        sb.AppendLine("If this is unexpected (wrong folder, unplugged drive), press No and check the folder.");
        sb.AppendLine("Deleted files stay recoverable from the server history for 30 days.");
        sb.AppendLine();
        sb.AppendLine("Allow these deletions once?");
        if (MessageBox.Show(sb.ToString(), "Tether – allow deletions?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            _session.Runner.ApproveDeletions();
            _allowDeletes.Visible = false;
        }
    }

    private void FixBlocked()
    {
        if (_session?.Runner.LastResult is not { Outcome: PassOutcome.Blocked } result)
            return;
        switch (result.BlockReason)
        {
            case BlockReason.ForeignMarker:
                if (MessageBox.Show($"{_settings.Folder} was synced by Tether before, but this PC has no record of it (for example after reinstalling).\n\n" +
                        "Continue syncing it? Files are merged: nothing is deleted, differing files become conflict copies.",
                        "Tether", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _session.Engine.AdoptExistingMarker();
                    _session.Runner.RequestSync("adopted", full: true);
                }
                break;
            case BlockReason.ServerChanged:
            case BlockReason.ServerRolledBack:
                if (MessageBox.Show(result.Message + "\n\nRe-link? Tether will merge this folder with the server: nothing is deleted or overwritten, " +
                        "files that differ become conflict copies.", "Tether", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _session.Engine.RelinkToServer();
                    _session.Runner.RequestSync("relinked", full: true);
                }
                break;
            default:
                LocateFolder(result);
                break;
        }
    }

    private void LocateFolder(PassResult result)
    {
        MessageBox.Show(result.Message + "\n\nIf the drive is unplugged, plug it in and choose Sync now. If you moved or renamed the folder, choose its new location next.",
            "Tether", MessageBoxButton.OK, MessageBoxImage.Warning);
        var dialog = new OpenFolderDialog { Title = "Where is your Tether folder now?" };
        if (dialog.ShowDialog() != true)
            return;
        var newFolder = dialog.FolderName;
        var marker = StateLocator.ReadMarker(newFolder);
        var oldMarker = _session?.State.MarkerId;
        if (marker is null || !string.Equals(marker, oldMarker, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("That folder is not your Tether folder (its .tether-marker is missing or different). Nothing was changed.",
                "Tether", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oldStateDir = Path.GetDirectoryName(_session!.State.DatabasePath)!;
        StopSession();
        StateLocator.AdoptState(oldStateDir, newFolder);
        _settings.Folder = newFolder;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        StartSession(null);
    }

    private void ShowWarnings()
    {
        var warnings = _session?.State.LoadWarnings().Values.ToList() ?? [];
        if (warnings.Count == 0)
        {
            MessageBox.Show("No files need attention.", "Tether");
            return;
        }
        var sb = new StringBuilder("These files are not synced until you rename them:\n\n");
        foreach (var w in warnings.Take(30))
            sb.AppendLine($"• {w.Path}\n   {w.Message}");
        MessageBox.Show(sb.ToString(), "Tether – files needing attention", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowSettings(bool firstRun)
    {
        var window = new SettingsWindow(_settings, _protector, firstRun);
        if (window.ShowDialog() != true || window.Result is null)
        {
            if (firstRun && !_settings.IsComplete)
                UpdateStatus(RunnerStatus.Offline, "Not set up yet – open Settings");
            return;
        }
        var folderChanged = !string.Equals(_settings.Folder, window.Result.Folder, StringComparison.OrdinalIgnoreCase);
        _settings = window.Result;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        SetAutoStart(_settings.StartWithWindows);
        if (folderChanged)
            _log.LogInformation("Sync folder set to {Folder}", _settings.Folder);
        StartSession(window.PlainToken);
    }

    private void TogglePause()
    {
        if (_session is null)
            return;
        _settings.Paused = !_settings.Paused;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        if (_settings.Paused)
            _session.Runner.Pause();
        else
            _session.Runner.Resume();
        _pause.Text = _settings.Paused ? "Resume syncing" : "Pause syncing";
    }

    private void SetAutoStart(bool enabled)
    {
        try
        {
            AutoStart.Set(enabled);
            _settings.StartWithWindows = enabled;
            SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            MessageBox.Show("Could not change the startup setting: " + ex.Message, "Tether");
        }
    }

    private static bool SafeIsAutoStart()
    {
        try
        {
            return AutoStart.IsEnabled();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void OpenFolder()
    {
        if (_settings.Folder is { } f && Directory.Exists(f))
            Shell(f);
    }

    private void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _log.LogWarning("Cannot open {Path}: {Error}", path, ex.Message);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _log.LogInformation("Resumed from sleep: catching up");
            _session?.Runner.NotifyResume();
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopSession();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
