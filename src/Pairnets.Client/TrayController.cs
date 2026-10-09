using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pairnets.Client.Platform;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Logging;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Forms = System.Windows.Forms;

namespace Pairnets.Client;

/// <summary>
/// Owns the tray icon, the main window, notifications and the <see cref="ClientSession"/>. All
/// sync decisions live in Pairnets.Core; this class reflects state and forwards clicks.
/// </summary>
public sealed class TrayController : ITrayActions, IDisposable
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
    private TrayPanel? _panel;
    private Action? _balloonClick;
    private volatile bool _dirty = true;
    private RunnerStatus? _shownStatus;
    private readonly UpdateService _updates = new(new UpdateChecker(new HttpClient()), PairnetsInfo.ProductVersion, UpdateChecker.AssetForThisPlatform());
    private bool _updateDismissed;
    private double? _updateProgress;
    private string? _updateError;
    private string? _serverPromptShownFor;
    private bool _lowSpaceNoticed;

    private readonly Forms.ToolStripMenuItem _openApp = new("Open Pairnets") { Font = new System.Drawing.Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
    private readonly Forms.ToolStripMenuItem _statusItem = new("Starting…") { Enabled = false };
    private readonly Forms.ToolStripMenuItem _syncNow = new("Sync now");
    private readonly Forms.ToolStripMenuItem _openFolder = new("Open folder");
    private readonly Forms.ToolStripMenuItem _fixProblem = new("Fix…") { Visible = false };
    private readonly Forms.ToolStripMenuItem _settingsItem = new("Settings…");
    private readonly Forms.ToolStripMenuItem _viewLog = new("View log");
    private readonly Forms.ToolStripMenuItem _reportBug = new("Report a bug…");
    private readonly Forms.ToolStripMenuItem _pause = new("Pause syncing");
    private readonly Forms.ToolStripMenuItem _autoStart = new("Start with Windows") { CheckOnClick = true };
    private readonly Forms.ToolStripMenuItem _exit = new("Exit");

    public TrayController(App app, ILoggerFactory loggers, RollingFileLoggerProvider fileLog)
    {
        _app = app;
        _loggers = loggers;
        _fileLog = fileLog;
        _log = loggers.CreateLogger("Pairnets.Tray");
        _settings = SettingsStore.Load(SettingsStore.DefaultPath);
        _fileLog.Minimum = _settings.DebugMode ? LogLevel.Debug : LogLevel.Information; // Debug mode: more detail in the log

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([_openApp, _statusItem, new Forms.ToolStripSeparator(), _syncNow, _openFolder, _fixProblem,
            new Forms.ToolStripSeparator(), _settingsItem, _viewLog, _reportBug, _pause, _autoStart, new Forms.ToolStripSeparator(), _exit]);
        _openApp.Click += (_, _) => ShowMainWindow();
        _syncNow.Click += (_, _) => SyncNow();
        _openFolder.Click += (_, _) => OpenFolder();
        _fixProblem.Click += (_, _) => FixBlocked();
        _settingsItem.Click += (_, _) => ShowMainWindow(MainPage.Settings);
        _viewLog.Click += (_, _) => ViewLog();
        _reportBug.Click += (_, _) => ReportBug();
        _pause.Click += (_, _) => TogglePause();
        _autoStart.Click += (_, _) => SetAutoStart(_autoStart.Checked);
        _exit.Click += (_, _) => Exit();
        menu.Opening += (_, _) => _autoStart.Checked = SafeIsAutoStart();

        _icon = new Forms.NotifyIcon
        {
            Icon = TrayIcons.For(RunnerStatus.Offline),
            Text = "Pairnets",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // A click opens the quick-look panel next to the taskbar (like OneDrive); "Open Pairnets" in it,
        // a double-click or the menu opens the full window.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                TogglePanel();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                _panel?.Hide();
                ShowMainWindow();
            }
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
            Toast("update:" + u.Version, "Pairnets update available", $"Version {u.Version} is ready. Open Pairnets to update.", Forms.ToolTipIcon.Info, ShowMainWindow);
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
            Toast("token", "Pairnets needs the token again", "The saved token cannot be read on this Windows account. Open Settings and enter it.", Forms.ToolTipIcon.Warning, () => ShowSettings(false));
            return;
        }

        var session = ClientSession.Start(_settings, plainToken, new RecycleBinTrash(_loggers.CreateLogger("Pairnets.Trash")), _loggers);
        session.StatusChanged += _ => _dirty = true;
        session.Activity.Added += _ => _dirty = true;
        session.ConflictCreated += c => _app.RunOnUi(() => Toast("conflict:" + c.Path, "Conflict: both computers changed a file",
            $"{c.Path} was changed on both. Your version was kept as \"{PathRules.FileName(c.ConflictCopyPath)}\".", Forms.ToolTipIcon.Warning, ShowMainWindow));
        session.PathWarningRaised += w => _app.RunOnUi(() => Toast("warning:" + w.Path, w.Code == ErrorCodes.CaseCollision ? "Name collision" : "File name not allowed",
            $"{w.Path}: {w.Message}", Forms.ToolTipIcon.Warning, ShowMainWindow));
        session.PassCompleted += r => _app.RunOnUi(() => OnPassCompleted(r));
        session.CatchUpCompleted += n => _app.RunOnUi(() => Toast("catchup", "Pairnets is up to date",
            $"Synced {n} change(s) made while this computer was away.", Forms.ToolTipIcon.Info, ShowMainWindow));
        session.ServerInfoChanged += info => _app.RunOnUi(() => OnServerInfo(session, info));
        session.AccountChanged += (next, token) => _app.RunOnUi(() => OnAccountChanged(session, next, token));
        session.JoinRequested += r => _app.RunOnUi(() => Toast("join:" + r.Code, r.Title, $"Code {r.Code} · click to review it on your nest.",
            Forms.ToolTipIcon.Info, () => ReviewJoin(r)));
        _session = session;
        _dirty = true;
        _log.LogInformation("Syncing {Folder} with {Server} as {Device}", _settings.Folder, _settings.ServerUrl, _settings.DeviceName);
    }

    /// <summary>Opens the nest's page where a computer asking to join is allowed or turned away.</summary>
    private void ReviewJoin(JoinRequest request)
    {
        if (_session?.Status.ReviewUrl(request) is { } url)
            Shell(url);
        else
            ShowMainWindow(MainPage.Attention);
    }

    // ------------------------------------------------------------------ the nest: devices and signing out

    /// <summary>The two steps, with this nest's name (or, signed in with an account, the account) filled in.</summary>
    public void AddComputer() => MessageBox.Show(Relay.AddComputerSteps(_settings.ServerUrl, _settings.AccountEmail, _session?.Status.NestUrl ?? NestFromSettings()),
        "Pairnets – add a computer", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>The nest's Devices page, or the account page when this computer signed in with a Pairnets account.</summary>
    public void ManageDevices()
    {
        if (Relay.ManageComputersUrl(_settings.ServerUrl, _session?.Status.NestUrl ?? NestFromSettings()) is { } url)
            Shell(url);
        else
            MessageBox.Show("Your nest has no website yet. On the server, give it its own name with: sudo ./install.sh --public-url https://nest.example.com",
                "Pairnets", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The nest's website when this computer already talks to it over HTTPS.</summary>
    private string? NestFromSettings() =>
        Uri.TryCreate(_settings.ServerUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>
    /// "Sign out of this computer": removes its key on the nest (best effort), stops syncing, forgets the key,
    /// and offers to sign in again. The files in the folder stay.
    /// </summary>
    public async void SignOut()
    {
        if (MessageBox.Show($"Sign out of this computer?\n\nSyncing stops. Your files in {_settings.Folder} stay here. Sign in again to continue.",
                "Pairnets – sign out", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var removed = false;
        try
        {
            removed = _session is not null && await _session.SignOutAsync();
        }
        catch (Exception ex) when (ex is Pairnets.Core.Api.PairnetsNetworkException or Pairnets.Core.Api.PairnetsAuthException or Pairnets.Core.Api.PairnetsProtocolException)
        {
            _log.LogWarning("Could not remove this computer on the nest: {Error}", ex.Message);
        }
        StopSession();
        _settings.ProtectedToken = null;
        _settings.DeviceId = null;
        _settings.FirstRunCompleted = false;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        _dirty = true;
        if (!removed)
            MessageBox.Show("Signed out here, but your nest could not be told. " + Relay.RemoveByHandHint(_settings.ServerUrl),
                "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
        _window?.Navigate(MainPage.Overview);
        ShowSettings(firstRun: true);
    }

    private bool _resetting;

    /// <summary>
    /// "Reset this app": removes this computer from the nest (best effort), stops syncing, forgets every setting, the
    /// saved key and the sync notes, and starts again from the sign-in window. The files in the folder stay.
    /// </summary>
    public async void ResetEverything()
    {
        if (_resetting)
            return;
        var folder = _settings.Folder;
        var where = string.IsNullOrWhiteSpace(folder) ? "your sync folder" : folder;
        if (MessageBox.Show($"Reset Pairnets on this computer?\n\nThis signs this computer out of your nest, forgets every setting and the saved sign-in, and removes Pairnets' sync notes. Your files in {where} are not deleted. The log files are kept.\n\nYou start again from the sign-in screen.",
                "Pairnets – reset", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        _resetting = true;
        try
        {
            var hadOwnKey = _settings.HasOwnKey;
            var serverUrl = _settings.ServerUrl;
            var removed = false;
            try
            {
                removed = _session is not null && await _session.SignOutAsync();
            }
            catch (Exception ex) when (ex is Pairnets.Core.Api.PairnetsNetworkException or Pairnets.Core.Api.PairnetsAuthException or Pairnets.Core.Api.PairnetsProtocolException)
            {
                _log.LogWarning("Could not remove this computer on the nest: {Error}", ex.Message);
            }
            StopSession(); // the sync notes are only free to delete once the session is gone
            var result = LocalReset.Run(_settings, _protector);
            var notes = new List<string>(result.Problems);
            try
            {
                AutoStart.Set(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                notes.Add("Start with Windows could not be turned off: " + ex.Message);
            }
            if (hadOwnKey && !removed)
                notes.Add("Your nest could not be told. " + Relay.RemoveByHandHint(serverUrl));
            _log.LogInformation("Pairnets was reset on this computer ({Folders} sync folder(s) cleared, {Problems} problem(s))", result.StateFoldersRemoved, result.Problems.Count);

            _settings = new ClientSettings();
            _fileLog.Minimum = LogLevel.Information;
            _updates.SetEnabled(true);
            (_updateDismissed, _updateProgress, _updateError, _serverPromptShownFor, _lowSpaceNoticed) = (false, null, null, null, false);
            _lastToast.Clear();
            _dirty = true;
            if (notes.Count > 0)
                MessageBox.Show("Pairnets was reset, with these notes:\n\n• " + string.Join("\n• ", notes), "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
            _window?.Navigate(MainPage.Overview);
            ShowSettings(firstRun: true);
        }
        finally
        {
            _resetting = false;
        }
    }

    /// <summary>This computer got its own key, moved to the nest's HTTPS name or was renamed: save and reconnect.</summary>
    private void OnAccountChanged(ClientSession from, ClientSettings next, string token)
    {
        if (!ReferenceEquals(_session, from))
            return; // settings were saved meanwhile; the new session checks again
        try
        {
            next.ProtectedToken = _protector.Protect(token);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not protect this computer's new key: {Error}", ex.Message);
            return;
        }
        _log.LogInformation("Saving this computer's sign-in: {Server} as {Device} ({Kind})", next.ServerUrl, next.DeviceName, next.HasOwnKey ? "own key" : "shared token");
        ApplySettings(next, token);
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

    private int _ticks;

    private void RefreshIfDirty()
    {
        _session?.PruneJoinRequests();
        // Redraw every 2 seconds while a window shows, so "2 min ago" and the map's moving dots stay current.
        if (++_ticks % 8 == 0 && (_window is { IsVisible: true } || _panel is { IsVisible: true }))
            _dirty = true;
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
        var tip = $"Pairnets: {line} ({status.LastSyncText.ToLowerInvariant()})";
        _icon.Text = tip.Length > 127 ? tip[..124] + "..." : tip;
        _statusItem.Text = line.Length > 80 ? line[..77] + "..." : line;
        _fixProblem.Visible = status.FixLabel is not null;
        _fixProblem.Text = status.FixLabel ?? "Fix…";
        _pause.Text = status.Paused ? "Resume syncing" : "Pause syncing";

        if (_window is { IsVisible: true })
        {
            DrawUpdateBanner();
            _window.ShowStatus(status, _settings.Folder, _settings.DeviceName);
            _window.ShowActivity(_session?.Activity.Items ?? []);
            _window.ShowAttention(BuildAttention(status));
        }
        if (_panel is { IsVisible: true })
        {
            _panel.ShowStatus(status, _settings.DeviceName);
            _panel.ShowActivity(_session?.Activity.Items ?? []);
            _panel.ShowAttention(BuildAttention(status).Count);
        }
    }

    private List<AttentionItem> BuildAttention(StatusSnapshot status)
    {
        var items = new List<AttentionItem>();
        if (_session is null)
            return items;
        foreach (var join in status.JoinRequests)
        {
            items.Add(new AttentionItem(join.Title, $"Code {join.Code}. Check that it matches the code on that computer, then allow or deny it on your nest.",
                "Review in browser", () => ReviewJoin(join)));
        }
        if (status.FixLabel is not null)
            items.Add(new AttentionItem("Syncing is paused until you decide", _session.Status.Text, status.FixLabel, FixBlocked));
        foreach (var w in _session.Warnings())
            items.Add(new AttentionItem(w.Path, w.Message ?? w.Code, "Show in folder", () => RevealFile(w.Path)));
        foreach (var c in _session.Activity.Items.Where(i => i.Kind == Pairnets.Core.Client.ActivityKind.Conflict && i.Path is not null).Take(20))
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
            var title = deletions ? "Deletions blocked"
                : result.BlockReason == BlockReason.SignedOut ? "Signed out of your nest"
                : result.BlockReason == BlockReason.SignInRequired ? "Sign in to your nest" : "Pairnets paused syncing";
            Toast("blocked:" + result.BlockReason, title, result.Message ?? result.BlockReason.ToString(),
                result.BlockReason == BlockReason.SignedOut ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning, FixBlocked);
        }
        else if (result.Outcome == PassOutcome.AuthFailed)
        {
            Toast("auth", "Sign in to your nest", "Your nest did not accept this computer's sign-in. Sign in again to keep syncing.", Forms.ToolTipIcon.Error, () => ShowSettings(true));
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
        var opening = _window is not { IsVisible: true };
        _window ??= new MainWindow(this);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        if (opening && _session is { } session)
            _ = session.RefreshDevicesAsync(); // the other computer's state, fresh
        _dirty = true;
        RefreshIfDirty();
    }

    /// <summary>Opens the main window on one page.</summary>
    public void ShowMainWindow(MainPage page)
    {
        ShowMainWindow();
        if (page == MainPage.Settings)
            ShowSettingsPage();
        else
            _window?.Navigate(page);
    }

    /// <summary>
    /// A pairnets:// link from the nest's website: bring whatever Pairnets is showing to the front.
    /// During first-time setup that is the sign-in dialog, which is just about to finish on its own.
    /// </summary>
    public void ComeToFront()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is SettingsWindow { IsVisible: true })
            {
                window.Activate();
                return;
            }
        }
        ShowMainWindow();
    }

    public void OpenWindow(MainPage page) => ShowMainWindow(page);

    public void Quit() => Exit();

    public IHistorySource? History => _session;

    /// <summary>The Devices page was opened: ask the server for the current list.</summary>
    public void RefreshDevices()
    {
        if (_session is { } session)
            _ = session.RefreshDevicesAsync();
    }

    /// <summary>The quick-look panel next to the taskbar; a second click on the icon closes it.</summary>
    private void TogglePanel()
    {
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSettings(firstRun: true);
            return;
        }
        _panel ??= new TrayPanel(this);
        if (_panel.IsVisible || DateTime.UtcNow - _panel.HiddenAt < TimeSpan.FromMilliseconds(300))
        {
            _panel.Hide();
            return;
        }
        if (_session is { } session)
            _ = session.RefreshDevicesAsync();
        var cursor = Forms.Cursor.Position;
        var screen = Forms.Screen.FromPoint(cursor);
        _panel.ShowStatus(_session?.Status ?? StatusSnapshot.Initial, _settings.DeviceName);
        _panel.ShowActivity(_session?.Activity.Items ?? []);
        _panel.ShowAttention(BuildAttention(_session?.Status ?? StatusSnapshot.Initial).Count);
        _panel.Open(cursor, screen.Bounds, screen.WorkingArea);
        _dirty = true;
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
                    && MessageBox.Show(_session.DescribePendingDeletes(), "Pairnets – allow deletions?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                    _session.ApproveDeletions();
                break;
            case BlockReason.ForeignMarker:
                if (MessageBox.Show($"{_settings.Folder} was synced by Pairnets before, but this computer has no record of it (for example after reinstalling).\n\n" +
                        "Continue syncing it? Files are merged: nothing is deleted, differing files become conflict copies.",
                        "Pairnets", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _session.AdoptExistingMarker();
                break;
            case BlockReason.ServerChanged:
            case BlockReason.ServerRolledBack:
                if (MessageBox.Show(status.Text + "\n\nRe-link? Pairnets will merge this folder with the server: nothing is deleted or overwritten, " +
                        "files that differ become conflict copies.", "Pairnets", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    _session.RelinkToServer();
                break;
            case BlockReason.FolderMissing:
            case BlockReason.MarkerMissing:
            case BlockReason.MarkerMismatch:
                LocateFolder(status.Text);
                break;
            case BlockReason.SignedOut:
            case BlockReason.SignInRequired:
                ShowSettings(firstRun: true);
                break;
        }
        _dirty = true;
    }

    private void LocateFolder(string message)
    {
        MessageBox.Show(message + "\n\nIf the drive is unplugged, plug it in and choose Sync now. If you moved or renamed the folder, choose its new location next.",
            "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
        var dialog = new OpenFolderDialog { Title = "Where is your Pairnets folder now?" };
        if (dialog.ShowDialog() != true || _session is null)
            return;
        var error = _session.CheckMovedFolder(dialog.FolderName);
        if (error is not null)
        {
            MessageBox.Show(error, "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(ex.Message, "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _settings.Folder = dialog.FolderName;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        StartSession(null);
    }

    /// <summary>Signing in is a window of its own; later, Settings is a page of the main window.</summary>
    public void ShowSettings(bool firstRun)
    {
        if (!firstRun && _settings.IsComplete && _settings.FirstRunCompleted)
        {
            ShowMainWindow(MainPage.Settings);
            return;
        }
        var window = new SettingsWindow(_settings, _protector, Shell, _session?.Status.NestUrl);
        if (window.ShowDialog() != true || window.Result is null)
            return;
        ApplySettings(window.Result, window.PlainToken);
        ShowMainWindow();
    }

    private void ShowSettingsPage()
    {
        if (_window is null)
            return;
        var view = new SettingsView(_settings, _protector, _updates, _session?.Status.ServerVersionText);
        view.Saved += v =>
        {
            ApplySettings(v.Result!, v.PlainToken);
            _window?.Navigate(MainPage.Overview);
        };
        view.SignOutRequested += SignOut;
        view.ResetRequested += ResetEverything;
        view.ManageDevicesRequested += ManageDevices;
        view.Cancelled += () => _window?.Navigate(MainPage.Overview);
        _window.ShowSettingsPage(view);
    }

    private void ApplySettings(ClientSettings settings, string? plainToken)
    {
        _settings = settings;
        _fileLog.Minimum = _settings.DebugMode ? LogLevel.Debug : LogLevel.Information;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        SetAutoStart(_settings.StartWithWindows);
        _updates.SetEnabled(_settings.CheckForUpdates);
        StartSession(plainToken);
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
            _window.ShowUpdate($"Pairnets {update.Version} could not be installed", _updateError, "Open download page");
        else if (_updateProgress is { } p)
            _window.ShowUpdate($"Downloading Pairnets {update.Version}…", "Syncing continues while it downloads.", "Update now", p, busy: true);
        else
            _window.ShowUpdate($"Pairnets {update.Version} is available",
                $"You have {PairnetsInfo.ProductVersion}. The update takes about 10 seconds and Pairnets restarts by itself.",
                IsInstalledCopy ? "Update now" : "Download");
    }

    /// <summary>True when this Pairnets.exe was put there by PairnetsSetup.exe (so the installer can replace it).</summary>
    private static bool IsInstalledCopy
    {
        get
        {
            var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pairnets");
            return string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), installDir, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Downloads PairnetsSetup.exe, checks it against SHA256SUMS.txt and runs it silently; the
    /// installer closes this Pairnets, replaces it and starts the new one. A copy started from the
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
        var setup = Path.Combine(Path.GetTempPath(), $"PairnetsSetup-{update.Version}.exe");
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
            _log.LogInformation("Installing Pairnets {Version}", update.Version);
            Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            Exit();
        }
        catch (Exception ex) when (ex is UpdateVerificationException or HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning("Update failed: {Error}", ex.Message);
            _updateError = ex is UpdateVerificationException
                ? "The download didn't match its checksum, so Pairnets didn't install it and kept your current version."
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

    /// <summary>The main window's "Update server" / "Check for update" button (works even when the prompt was skipped).</summary>
    public void UpdateServer()
    {
        if (_session is { } session && session.Status.Server is { } info)
            ShowServerUpdate(info.ServerVersion ?? string.Empty, null);
    }

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
        if (_settings.AutoUpdateServer)
        {
            UpdateServerQuietly(session, info.ServerVersion!);
            return;
        }
        ShowServerUpdate(info.ServerVersion!, null);
    }

    private async void UpdateServerQuietly(ClientSession session, string serverVersion)
    {
        ServerUpdateResult result;
        try
        {
            result = await session.UpdateServerQuietlyAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Automatic server update failed");
            return;
        }
        _dirty = true;
        if (result.Success && !result.AlreadyUpToDate)
            Toast("server-updated", "Server updated", $"Your server now runs {result.ServerVersion}.", Forms.ToolTipIcon.Info, ShowMainWindow);
        else if (!result.CanUpdateItself)
            ShowServerUpdate(serverVersion, result); // needs the one-time setup
    }

    private void ShowServerUpdate(string serverVersion, ServerUpdateResult? result)
    {
        var dialog = new ServerUpdateWindow(() => _session, serverVersion, debug: () => _settings.DebugMode); // saving Settings replaces the session
        if (result is not null)
            dialog.ShowResult(result);
        dialog.Closed += (_, _) =>
        {
            if (dialog.Skipped)
                _settings.SkippedServerVersion = dialog.ServerVersion;
            if (dialog.AlwaysUpdate)
                _settings.AutoUpdateServer = true;
            if (dialog.Skipped || dialog.AlwaysUpdate)
                SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        };
        dialog.Show();
    }

    public void OpenFolder()
    {
        if (_settings.Folder is { } f && Directory.Exists(f))
            Shell(f);
    }

    void IMainActions.ShowSettings()
    {
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
            ShowSettings(firstRun: true);
        else
            ShowSettingsPage();
    }

    public void ViewLog() => Shell(_fileLog.CurrentFile);

    /// <summary>"Report a bug": a report to paste to whoever helps (copied and saved; nothing is sent).</summary>
    public void ReportBug() => ShowBugReport(null);

    /// <summary>Offered after an unexpected error: the same report, with that error in it.</summary>
    public void ReportBug(Exception error) => ShowBugReport(error);

    private void ShowBugReport(Exception? error)
    {
        var settings = _settings;
        var session = _session;
        var window = new BugReportWindow(() => BugReport.BuildAsync(settings, session, _fileLog.CurrentFile, error, "Windows app"), Shell, afterError: error is not null);
        window.Show();
        window.Activate();
    }

    private string LocalPath(string syncPath) => Path.Combine(_settings.Folder ?? string.Empty, PathRules.ToOsRelative(syncPath));

    public void RevealFile(string syncPath)
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
            MessageBox.Show("Could not change the startup setting: " + ex.Message, "Pairnets");
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
        _panel?.Close();
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
