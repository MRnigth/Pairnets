using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Tether.Client.Platform;
using Tether.Client.Ui;
using Tether.Core;
using Tether.Core.Client;
using Tether.Core.Logging;
using Tether.Core.Paths;
using Tether.Core.Settings;
using Tether.Core.Sync;
using Forms = System.Windows.Forms;

namespace Tether.Client;

/// <summary>
/// Owns the tray icon, the main window, notifications and the <see cref="ClientSession"/>. All
/// sync decisions live in Tether.Core; this class reflects state and forwards clicks.
/// </summary>
public sealed class TrayController : IMainActions, IDisposable
{
    private readonly App _app;
    private readonly ILoggerFactory _loggers;
    private readonly RollingFileLoggerProvider _fileLog;
    private readonly ILogger _log;
    private readonly DpapiProtector _protector = new();
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<string, DateTime> _lastToast = [];
    private readonly DispatcherTimer _refresh;
    private ClientSettings _settings;
    private ClientSession? _session;
    private MainWindow? _window;
    private Action? _balloonClick;
    private volatile bool _dirty = true;
    private RunnerStatus? _shownStatus;
    private readonly UpdateService _updates = new(new UpdateChecker(new HttpClient()), TetherInfo.ProductVersion, UpdateChecker.AssetForThisPlatform());
    private bool _updateDismissed;
    private double? _updateProgress;
    private string? _updateError;
    private string? _serverPromptShownFor;
    private bool _lowSpaceNoticed;

    private readonly Forms.ToolStripMenuItem _openApp = new("Open Tether") { Font = new System.Drawing.Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
    private readonly Forms.ToolStripMenuItem _statusItem = new("Starting…") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _syncNow = new("Sync now");
    private readonly Forms.ToolStripMenuItem _openFolder = new("Open folder");
    private readonly Forms.ToolStripMenuItem _fixProblem = new("Fix…") { Visible = false };
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
        menu.Items.AddRange([_openApp, _statusItem, new Forms.ToolStripSeparator(), _syncNow, _openFolder, _fixProblem,
            new Forms.ToolStripSeparator(), _settingsItem, _viewLog, _pause, _autoStart, new Forms.ToolStripSeparator(), _exit]);
        _openApp.Click += (_, _) => ShowMainWindow();
        _syncNow.Click += (_, _) => SyncNow();
        _openFolder.Click += (_, _) => OpenFolder();
        _fixProblem.Click += (_, _) => FixBlocked();
        _settingsItem.Click += (_, _) => ShowSettings(firstRun: false);
        _viewLog.Click += (_, _) => ViewLog();
        _pause.Click += (_, _) => TogglePause();
        _autoStart.Click += (_, _) => SetAutoStart(_autoStart.Checked);
        _exit.Click += (_, _) => Exit();
        menu.Opening += (_, _) => _autoStart.Checked = SafeIsAutoStart();

        _icon = new Forms.NotifyIcon
        {
            Icon = TrayIcons.For(RunnerStatus.Offline),
            Text = "Tether",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowMainWindow();
        };
        _icon.BalloonTipClicked += (_, _) => (_balloonClick ?? ShowMainWindow).Invoke();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Progress events arrive for every transferred chunk: redraw at most 4 times a second.
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshIfDirty(), _app.Dispatcher);
        _refresh.Start();
    }

    public void Start()
    {
        _updates.UpdateAvailable += u => _app.RunOnUi(() =>
        {
            _updateDismissed = false;
            _updateError = null;
            _dirty = true;
            Toast("update:" + u.Version, "Tether update available", $"Version {u.Version} is ready. Open Tether to update.", Forms.ToolTipIcon.Info, ShowMainWindow);
        });
        _updates.SetEnabled(_settings.CheckForUpdates);
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSettings(firstRun: true);
            return;
        }
        StartSession(null);
        if (!Environment.GetCommandLineArgs().Contains("--autostart"))
            ShowMainWindow();
    }

    // ------------------------------------------------------------------ session

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
            return;
        }

        var session = ClientSession.Start(_settings, plainToken, new RecycleBinTrash(_loggers.CreateLogger("Tether.Trash")), _loggers);
        session.StatusChanged += _ => _dirty = true;
        session.Activity.Added += _ => _dirty = true;
        session.ConflictCreated += c => _app.RunOnUi(() => Toast("conflict:" + c.Path, "Conflict: both computers changed a file",
            $"{c.Path} was changed on both. Your version was kept as \"{PathRules.FileName(c.ConflictCopyPath)}\".", Forms.ToolTipIcon.Warning, ShowMainWindow));
        session.PathWarningRaised += w => _app.RunOnUi(() => Toast("warning:" + w.Path, w.Code == ErrorCodes.CaseCollision ? "Name collision" : "File name not allowed",
            $"{w.Path}: {w.Message}", Forms.ToolTipIcon.Warning, ShowMainWindow));
        session.PassCompleted += r => _app.RunOnUi(() => OnPassCompleted(r));
        session.CatchUpCompleted += n => _app.RunOnUi(() => Toast("catchup", "Tether is up to date",
            $"Synced {n} change(s) made while this computer was away.", Forms.ToolTipIcon.Info, ShowMainWindow));
        session.ServerInfoChanged += info => _app.RunOnUi(() => OnServerInfo(session, info));
        _session = session;
        _dirty = true;
        _log.LogInformation("Syncing {Folder} with {Server} as {Device}", _settings.Folder, _settings.ServerUrl, _settings.DeviceName);
    }

    private void StopSession()
    {
        if (_session is null)
            return;
        var s = _session;
        _session = null;
        Task.Run(async () => await s.DisposeAsync()).Wait(TimeSpan.FromSeconds(30));
        _dirty = true;
    }

    // ------------------------------------------------------------------ drawing

    private void RefreshIfDirty()
    {
        if (!_dirty)
            return;
        _dirty = false;
        var status = _session?.Status ?? StatusSnapshot.Initial with { Text = "Not set up yet: open Settings" };

        if (_shownStatus != status.Status)
        {
            _icon.Icon = TrayIcons.For(status.Status);
            _shownStatus = status.Status;
        }
        var line = status.IsTransferring
            ? $"{Math.Min(status.FilesDone + 1, Math.Max(status.FilesTotal, 1))}/{status.FilesTotal}: {PathRules.FileName(status.CurrentPath!)}{(status.Percent is { } p ? $" {p}%" : string.Empty)}"
            : status.Text;
        var tip = $"Tether: {line} ({status.LastSyncText.ToLowerInvariant()})";
        _icon.Text = tip.Length > 127 ? tip[..124] + "..." : tip;
        _statusItem.Text = line.Length > 80 ? line[..77] + "..." : line;
        _fixProblem.Visible = status.FixLabel is not null;
        _fixProblem.Text = status.FixLabel ?? "Fix…";
        _pause.Text = status.Paused ? "Resume syncing" : "Pause syncing";

        if (_window is { IsVisible: true })
        {
            DrawUpdateBanner();
            _window.ShowStatus(status, _settings.Folder);
            _window.ShowActivity(_session?.Activity.Items ?? []);
            _window.ShowAttention(BuildAttention(status));
        }
    }

    private List<AttentionItem> BuildAttention(StatusSnapshot status)
    {
        var items = new List<AttentionItem>();
        if (_session is null)
            return items;
        if (status.FixLabel is not null)
            items.Add(new AttentionItem("Syncing is paused until you decide", _session.Status.Text, status.FixLabel, FixBlocked));
        foreach (var w in _session.Warnings())
            items.Add(new AttentionItem(w.Path, w.Message ?? w.Code, "Show in folder", () => RevealFile(w.Path)));
        foreach (var c in _session.Activity.Items.Where(i => i.Kind == Tether.Core.Client.ActivityKind.Conflict && i.Path is not null).Take(20))
        {
            if (File.Exists(LocalPath(c.Path!)))
                items.Add(new AttentionItem("Conflict copy: " + PathRules.FileName(c.Path!), "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => RevealFile(c.Path!)));
        }
        return items;
    }

    private void OnPassCompleted(PassResult result)
    {
        if (result.Outcome == PassOutcome.Blocked)
        {
            var deletions = result.BlockReason is BlockReason.MassDelete or BlockReason.FolderEmpty;
            Toast("blocked:" + result.BlockReason, deletions ? "Deletions blocked" : "Tether paused syncing",
                result.Message ?? result.BlockReason.ToString(), Forms.ToolTipIcon.Warning, FixBlocked);
        }
        else if (result.Outcome == PassOutcome.AuthFailed)
        {
            Toast("auth", "The server rejected the token", "Open Settings and enter the token printed by install.sh.", Forms.ToolTipIcon.Error, () => ShowSettings(false));
        }
        _dirty = true;
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

    // ------------------------------------------------------------------ actions (also used by MainWindow)

    public void ShowMainWindow()
    {
        if (!_settings.IsComplete)
        {
            ShowSettings(firstRun: true);
            return;
        }
        _window ??= new MainWindow(this);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        _dirty = true;
        RefreshIfDirty();
    }

    public void SyncNow() => _session?.SyncNow();

    public void TogglePause()
    {
        if (_session is null)
            return;
        if (_session.Status.Paused || _settings.Paused)
            _session.Resume();
        else
            _session.Pause();
        _settings.Paused = _session.Settings.Paused;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
    }

    public void FixBlocked()
    {
        if (_session is null)
            return;
        var status = _session.Status;
        switch (status.BlockReason)
        {
            case BlockReason.MassDelete:
            case BlockReason.FolderEmpty:
                if (_session.PendingDeletes().Count > 0
                    && MessageBox.Show(_session.DescribePendingDeletes(), "Tether – allow deletions?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                    _session.ApproveDeletions();
                break;
            case BlockReason.ForeignMarker:
                if (MessageBox.Show($"{_settings.Folder} was synced by Tether before, but this computer has no record of it (for example after reinstalling).\n\n" +
                        "Continue syncing it? Files are merged: nothing is deleted, differing files become conflict copies.",
                        "Tether", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _session.AdoptExistingMarker();
                break;
            case BlockReason.ServerChanged:
            case BlockReason.ServerRolledBack:
                if (MessageBox.Show(status.Text + "\n\nRe-link? Tether will merge this folder with the server: nothing is deleted or overwritten, " +
                        "files that differ become conflict copies.", "Tether", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    _session.RelinkToServer();
                break;
            case BlockReason.FolderMissing:
            case BlockReason.MarkerMissing:
            case BlockReason.MarkerMismatch:
                LocateFolder(status.Text);
                break;
        }
        _dirty = true;
    }

    private void LocateFolder(string message)
    {
        MessageBox.Show(message + "\n\nIf the drive is unplugged, plug it in and choose Sync now. If you moved or renamed the folder, choose its new location next.",
            "Tether", MessageBoxButton.OK, MessageBoxImage.Warning);
        var dialog = new OpenFolderDialog { Title = "Where is your Tether folder now?" };
        if (dialog.ShowDialog() != true || _session is null)
            return;
        var error = _session.CheckMovedFolder(dialog.FolderName);
        if (error is not null)
        {
            MessageBox.Show(error, "Tether", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oldStateDir = _session.StateDirectory;
        StopSession();
        try
        {
            StateLocator.AdoptState(oldStateDir, dialog.FolderName);
        }
        catch (IOException ex)
        {
            MessageBox.Show(ex.Message, "Tether", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _settings.Folder = dialog.FolderName;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        StartSession(null);
    }

    public void ShowSettings(bool firstRun)
    {
        var window = new SettingsWindow(_settings, _protector, firstRun, _updates);
        if (window.ShowDialog() != true || window.Result is null)
            return;
        _settings = window.Result;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        SetAutoStart(_settings.StartWithWindows);
        _updates.SetEnabled(_settings.CheckForUpdates);
        StartSession(window.PlainToken);
        ShowMainWindow();
    }

    // ------------------------------------------------------------------ updates and the server

    private void DrawUpdateBanner()
    {
        if (_window is null)
            return;
        if (_updates.Available is not { } update || _updateDismissed)
        {
            _window.ShowUpdate(null, string.Empty, string.Empty);
            return;
        }
        if (_updateError is not null)
            _window.ShowUpdate($"Tether {update.Version} could not be installed", _updateError, "Open download page");
        else if (_updateProgress is { } p)
            _window.ShowUpdate($"Downloading Tether {update.Version}…", "Syncing continues while it downloads.", "Update now", p, busy: true);
        else
            _window.ShowUpdate($"Tether {update.Version} is available",
                $"You have {TetherInfo.ProductVersion}. The update takes about 10 seconds and Tether restarts by itself.",
                IsInstalledCopy ? "Update now" : "Download");
    }

    /// <summary>True when this Tether.exe was put there by TetherSetup.exe (so the installer can replace it).</summary>
    private static bool IsInstalledCopy
    {
        get
        {
            var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tether");
            return string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), installDir, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Downloads TetherSetup.exe, checks it against SHA256SUMS.txt and runs it silently; the
    /// installer closes this Tether, replaces it and starts the new one. A copy started from the
    /// zip (not installed) opens the download page instead.
    /// </summary>
    public async void UpdateNow()
    {
        if (_updates.Available is not { } update || _updateProgress is not null)
            return;
        if (_updateError is not null || !IsInstalledCopy)
        {
            Shell(update.ReleasePage.ToString());
            return;
        }
        var setup = Path.Combine(Path.GetTempPath(), $"TetherSetup-{update.Version}.exe");
        _updateProgress = 0;
        _dirty = true;
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                _updateProgress = p.Total is > 0 ? p.Done * 100.0 / p.Total.Value : 0;
                _dirty = true;
            });
            await new UpdateChecker(new HttpClient()).DownloadVerifiedAsync(update, setup, progress, CancellationToken.None);
            _log.LogInformation("Installing Tether {Version}", update.Version);
            Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            Exit();
        }
        catch (Exception ex) when (ex is UpdateVerificationException or HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning("Update failed: {Error}", ex.Message);
            _updateError = ex is UpdateVerificationException
                ? "The download didn't match its checksum, so Tether didn't install it and kept your current version."
                : "The download failed: " + ex.Message;
            _updateProgress = null;
            _dirty = true;
        }
    }

    public void DismissUpdate()
    {
        _updateDismissed = true;
        _dirty = true;
    }

    public void DownloadNow() => _session?.DownloadNow();

    private void OnServerInfo(ClientSession session, Core.ServerInfo info)
    {
        if (!ReferenceEquals(session, _session))
            return;
        if (session.Status.ServerSpaceLow && !_lowSpaceNoticed)
        {
            _lowSpaceNoticed = true;
            Toast("disk", "The server is running out of space", $"{session.Status.ServerFreeText}. Free up space on the server or delete old history.", Forms.ToolTipIcon.Warning, ShowMainWindow);
        }
        if (!session.ServerNeedsUpdate(info) || _serverPromptShownFor == info.ServerVersion)
            return;
        _serverPromptShownFor = info.ServerVersion;
        var dialog = new ServerUpdateWindow(session, info.ServerVersion!);
        dialog.Closed += (_, _) =>
        {
            if (!dialog.Skipped)
                return;
            _settings.SkippedServerVersion = dialog.ServerVersion;
            SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        };
        dialog.Show();
    }

    public void OpenFolder()
    {
        if (_settings.Folder is { } f && Directory.Exists(f))
            Shell(f);
    }

    void IMainActions.ShowSettings() => ShowSettings(firstRun: false);

    public void ViewLog() => Shell(_fileLog.CurrentFile);

    private string LocalPath(string syncPath) => Path.Combine(_settings.Folder ?? string.Empty, PathRules.ToOsRelative(syncPath));

    private void RevealFile(string syncPath)
    {
        var full = LocalPath(syncPath);
        try
        {
            if (File.Exists(full))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false });
            else
                OpenFolder();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.LogWarning("Cannot open Explorer: {Error}", ex.Message);
        }
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

    private void Exit()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _app.Shutdown();
    }

    public void Dispose()
    {
        _updates.Dispose();
        _refresh.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopSession();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
