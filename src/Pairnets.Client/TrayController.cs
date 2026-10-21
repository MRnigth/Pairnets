using Pairnets.Core.Api;
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
    /// <summary>How reports and crash cards name this app.</summary>
    public const string AppName = "Windows app";

    private readonly ILoggerFactory _loggers;
    private readonly RollingFileLoggerProvider _fileLog;
    private readonly ILogger _log;
    private readonly IWindowsPlatform _platform;
    private readonly ClientEnvironment _env;
    private readonly Dispatcher _ui;
    private readonly Action _shutdown;
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<string, DateTime> _lastToast = [];
    private readonly DispatcherTimer _refresh;
    private ClientSettings _settings;
    private ClientSession? _session;
    private Platform.CloudFiles.ICloudFolder? _cloud;
    private CancellationTokenSource? _leaving;
    private Task? _leaveTask;
    private MainWindow? _window;
    private TrayPanel? _panel;
    private Action? _balloonClick;
    private volatile bool _dirty = true;
    private RunnerStatus? _shownStatus;
    private readonly UpdateService _updates;
    private readonly UpdatePackage _package;
    private IDisposable? _activation;
    private bool _updateDismissed;
    private double? _updateProgress;
    private UpdateProblem? _updateError;
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

    /// <param name="platform">Windows itself: the saved key, the Recycle Bin, start with Windows, opening things.</param>
    /// <param name="env">Where the settings, sync notes and logs are, and where updates come from.</param>
    /// <param name="ui">The app's dispatcher: everything the session reports is drawn there.</param>
    /// <param name="shutdown">Ends the app (Exit, and after starting an update's installer).</param>
    public TrayController(ILoggerFactory loggers, RollingFileLoggerProvider fileLog, IWindowsPlatform platform, ClientEnvironment env,
        Dispatcher ui, Action shutdown)
    {
        _loggers = loggers;
        _fileLog = fileLog;
        _platform = platform;
        _env = env;
        _ui = ui;
        _shutdown = shutdown;
        _log = loggers.CreateLogger("Pairnets.Tray");
        _settings = SettingsStore.Load(env.SettingsPath, problem => _log.LogWarning("Settings: {Problem}", problem));
        UpgradeSettings();
        TakeSetupChoices();
        _package = UpdatePackage.ForThisComputer(IsInstalledCopy);
        _updates = new UpdateService(env.CreateUpdateChecker(), PairnetsInfo.ProductVersion, _package.Asset);
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
            Visible = env.ShowTrayIcon,
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
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshIfDirty(), ui);
        _refresh.Start();
    }

    /// <summary>Test hook: other sync timings (null in the app).</summary>
    internal Func<RunnerOptions, RunnerOptions>? RunnerOptionsForTests { get; init; }

    /// <summary>What Pairnets was started with (--after-update, the start-at-login flag, an Explorer command); tests set their own.</summary>
    internal string[] Arguments { get; init; } = Environment.GetCommandLineArgs();

    /// <summary>Raised with the title and text of every notification shown (tests read it; the icon is hidden there).</summary>
    internal event Action<string, string>? Notified;

    internal Forms.NotifyIcon TrayIcon => _icon;
    internal MainWindow? Window => _window;
    internal TrayPanel? Panel => _panel;
    internal ClientSession? Session => _session;
    internal ClientSettings Settings => _settings;

    /// <summary>Draws the current state right away instead of at the next tick.</summary>
    internal void RefreshNow()
    {
        _dirty = true;
        RefreshIfDirty();
    }

    private void RunOnUi(Action action)
    {
        if (_ui.CheckAccess())
            action();
        else
            _ui.BeginInvoke(DispatcherPriority.Normal, action);
    }

    public void Start()
    {
        // Opening Pairnets again (Start menu, a shortcut, a pairnets:// link from the nest's website, a right-click
        // item in Explorer) starts a second Pairnets, which pokes this one through the pipe and quits; come to the
        // front for it, or do what the right-click item asked. The poke is answered once that is done, so a stuck
        // app gets reported. Listening starts before the sign-in window below opens (and waits), because that is
        // when the link comes.
        _activation = AppActivation.Listen(message => _ui.InvokeAsync(() =>
        {
            if (FileManagerCommand.FromMessage(message) is { } command)
                HandleFileCommand(command);
            else
                ComeToFront();
        }).Task, _env.ActivationPipeName);
        _updates.UpdateAvailable += u => RunOnUi(() =>
        {
            _updateDismissed = false;
            _updateError = null;
            _dirty = true;
            if (!u.Signed)
                return; // (the banner says it was not installed; no "update available" toast for it)
            Toast("update:" + u.Version, "Pairnets update available", $"Version {u.Version} is ready. Open Pairnets to update.", Forms.ToolTipIcon.Info, ShowMainWindow);
        });
        _updates.SetEnabled(_settings.CheckForUpdates);
        NoticeLastClose();
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSettings(firstRun: true);
            return;
        }
        StartSession(null);
        if (FileManagerCommand.FromArguments(Arguments) is { } command)
            HandleFileCommand(command); // started by a right-click item in Explorer while Pairnets was not running
        else if (Arguments.Contains(SelfReplace.AfterUpdateArgument))
            Toast("updated", UpdateTexts.UpdatedTitle, UpdateTexts.Updated(PairnetsInfo.ProductVersion), Forms.ToolTipIcon.Info, ShowMainWindow);
        else if (!Arguments.Contains(AppActivation.AutoStartArgument))
            ShowMainWindow();
    }

    /// <summary>Pairnets had to close after an error since it last ran: say so once (the card is in "Report a bug").</summary>
    private void NoticeLastClose()
    {
        if (CrashCards.TakeNewCloses(_env.CrashesDir).Count > 0)
            Toast("crash", CrashNotice.Title, CrashNotice.Text, Forms.ToolTipIcon.Warning, ReportBug);
    }

    // ------------------------------------------------------------------ session

    private void StartSession(string? plainToken)
    {
        StopSession();
        try
        {
            plainToken ??= _platform.Secrets.Unprotect(_settings.ProtectedToken!);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Cannot decrypt the saved token: {Error}", ex.Message);
            Toast("token", "Pairnets needs the token again", "The saved token cannot be read on this Windows account. Open Settings and enter it.", Forms.ToolTipIcon.Warning, () => ShowSettings(false));
            return;
        }

        var cloud = StartCloudFolder();
        ClientSession session;
        try
        {
            session = ClientSession.Start(_settings, plainToken, _platform.CreateTrash(_loggers.CreateLogger("Pairnets.Trash")), _loggers,
                stateBaseDir: _env.LocalDir, runnerOptions: RunnerOptionsForTests, cloudFiles: cloud);
        }
        catch
        {
            cloud?.Dispose();
            throw;
        }
        if (cloud is not null)
        {
            _cloud = cloud;
            cloud.Fetch = session.FetchVersionAsync;
            session.PassCompleted += _ => cloud.Reconcile(session.SyncedHash);
            cloud.Reconcile(session.SyncedHash);
            if (_settings.CloudFolderWanted == CloudFolderMode.Off)
                _leaveTask = LeaveCloudFolderAsync(cloud);
        }
        session.StatusChanged += _ => _dirty = true;
        session.Activity.Added += _ => _dirty = true;
        session.ConflictCreated += c => RunOnUi(() => Toast("conflict:" + c.Path, "Conflict: both computers changed a file",
            $"{c.Path} was changed on both. Your version was kept as \"{PathRules.FileName(c.ConflictCopyPath)}\".", Forms.ToolTipIcon.Warning, ShowMainWindow));
        session.PathWarningRaised += w => RunOnUi(() => Toast("warning:" + w.Path, w.Code == ErrorCodes.CaseCollision ? "Name collision" : "File name not allowed",
            $"{w.Path}: {w.Message}", Forms.ToolTipIcon.Warning, ShowMainWindow));
        session.PassCompleted += r => RunOnUi(() => OnPassCompleted(r));
        session.CatchUpCompleted += n => RunOnUi(() => Toast("catchup", "Pairnets is up to date",
            $"Synced {n} change(s) made while this computer was away.", Forms.ToolTipIcon.Info, ShowMainWindow));
        session.ServerInfoChanged += info => RunOnUi(() => OnServerInfo(session, info));
        session.AccountChanged += (next, token) => RunOnUi(() => OnAccountChanged(session, next, token));
        session.Files.Opened.NestFullWhileSaving += _ => RunOnUi(() =>
        {
            if (ReferenceEquals(_session, session))
                _window?.ShowNestFull(); // a file opened from the nest could not be saved back
        });
        session.JoinRequested += r => RunOnUi(() => Toast("join:" + r.Code, r.Title, $"Code {r.Code} · click to review it on your nest.",
            Forms.ToolTipIcon.Info, () => ReviewJoin(r)));
        _session = session;
        _dirty = true;
        _log.LogInformation("Syncing {Folder} with {Server} as {Device}", _settings.Folder, _settings.ServerUrl, _settings.DeviceName);
        UpdateExplorerEntry();
    }

    // ------------------------------------------------------------------ File Explorer

    /// <summary>
    /// Puts Pairnets in File Explorer for the current folder (left side, right-click items, folder icon), or takes it
    /// away when switched off in Settings. Runs at every start, so a moved program or folder is followed.
    /// </summary>
    private void UpdateExplorerEntry()
    {
        var wanted = _settings.ShowInFileManager || _settings.ExplorerRightClick;
        var folder = wanted && _settings.IsComplete && Directory.Exists(_settings.Folder) ? _settings.Folder : null;
        try
        {
            // A Windows cloud folder is in the left side by itself (with Pairnets' icon), so only one "Pairnets" shows.
            if (!_platform.SetExplorerEntry(folder, leftSide: _settings.ShowInFileManager && _cloud is not { IsActive: true },
                    rightClick: _settings.ExplorerRightClick, folderIcon: _settings.ShowInFileManager))
                _log.LogInformation("Pairnets could not be fully added to File Explorer; it tries again at the next start");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not update Pairnets in File Explorer");
        }
    }

    /// <summary>
    /// The Pairnets folder as a Windows cloud folder when chosen in Settings (ticks in Explorer; Files On-Demand), or
    /// null for a plain folder. When it was turned off but Windows still has the cloud folder, it is started once more
    /// (keeping every file here) so that <see cref="LeaveCloudFolderAsync"/> can bring everything home and end it.
    /// </summary>
    private Platform.CloudFiles.ICloudFolder? StartCloudFolder()
    {
        var wanted = _settings.CloudFolderWanted;
        if (wanted == CloudFolderMode.Off && !SafeHasCloudFolder())
            return null;
        var mode = wanted == CloudFolderMode.Off ? CloudFolderMode.KeepAllHere : wanted;
        Platform.CloudFiles.ICloudFolder? cloud;
        string? problem;
        try
        {
            cloud = _platform.StartCloudFolder(_settings.Folder!, mode, _loggers.CreateLogger("Pairnets.CloudFolder"), out problem, _settings.Placement);
        }
        catch (Exception ex)
        {
            (cloud, problem) = (null, ex.Message);
        }
        if (cloud is null && wanted != CloudFolderMode.Off)
        {
            _log.LogWarning("The Pairnets folder stays a plain folder: {Problem}", problem);
            var onDemand = wanted == CloudFolderMode.OnDemand;
            Toast("cloudfolder", onDemand ? "Files On-Demand can't be used here" : "No ticks in Explorer for now",
                (problem ?? "Windows did not allow it.") + (onDemand ? " Your On-Demand folders are kept on this PC instead." : " Pairnets keeps syncing as before."),
                Forms.ToolTipIcon.Warning, () => ShowMainWindow(MainPage.Settings));
        }
        return cloud;
    }

    private bool SafeHasCloudFolder()
    {
        try
        {
            return _platform.HasCloudFolder();
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not ask Windows about cloud folders: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The switch was turned off: download whatever is only on the nest, then make the folder a plain folder again. If
    /// that cannot finish (no connection), the folder stays a cloud folder, keeping everything here, and it is tried again
    /// at the next start.
    /// </summary>
    private async Task LeaveCloudFolderAsync(Platform.CloudFiles.ICloudFolder cloud)
    {
        _leaving?.Cancel();
        var stop = _leaving = new CancellationTokenSource();
        string? problem;
        try
        {
            problem = await cloud.LeaveAsync(stop.Token).ConfigureAwait(false); // (StopSession may wait for this on the window's thread)
        }
        catch (Exception ex)
        {
            problem = ex is OperationCanceledException ? null : FriendlyErrors.Describe(ex);
            if (ex is OperationCanceledException)
                return;
        }
        RunOnUi(() =>
        {
            if (problem is null)
            {
                _log.LogInformation("The Pairnets folder is a plain folder again");
                UpdateExplorerEntry();
                Toast("cloudfolder", "Your Pairnets folder is a plain folder again", "Every file is on this PC.", Forms.ToolTipIcon.Info, OpenFolder);
            }
            else
            {
                _log.LogWarning("The Pairnets folder could not become a plain folder yet: {Problem}", problem);
                Toast("cloudfolder", "Still a cloud folder for now", problem + " Pairnets tries again at the next start.", Forms.ToolTipIcon.Warning, ShowMainWindow);
            }
        });
    }

    /// <summary>A right-click item in Explorer: "Open in Pairnets" or "Show sync status" for a file or folder.</summary>
    internal void HandleFileCommand(FileManagerCommand command)
    {
        if (_session is not { } session || _settings.Folder is not { } folder || FileStatusReader.ToSyncPath(folder, command.Path) is not { } rel)
        {
            ShowMainWindow();
            return;
        }
        if (command.Action == FileManagerAction.WhereItLives)
        {
            // A folder's own choice; for a file, the folder it is in.
            var folderPath = rel.Length == 0 || Directory.Exists(command.Path) ? rel : PathRules.Parent(rel) ?? string.Empty;
            ShowMainWindow();
            _window?.ShowFilesAt(folderPath, choosePlacement: true);
            return;
        }
        if (command.Action == FileManagerAction.Open)
        {
            if (rel.Length == 0 || Directory.Exists(command.Path))
            {
                ShowMainWindow(MainPage.History);
                if (_window is { } w)
                    w.HistoryPane.SearchText = rel;
            }
            else
            {
                ShowMainWindow();
                _window?.ShowVersionsFor(rel);
            }
            return;
        }
        var status = session.GetFileStatus(command.Path);
        var name = rel.Length == 0 ? "Your Pairnets folder" : PathRules.FileName(rel);
        if (status.State == FileSyncState.Problem)
        {
            if (Dialogs.Ask(null, $"{name}\n\n{status.Text}\n\nOpen Pairnets to see what needs you?", "Pairnets – sync status",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                ShowMainWindow(MainPage.Attention);
            return;
        }
        Dialogs.Ask(null, $"{name}\n\n{status.Text}", "Pairnets – sync status", MessageBoxButton.OK,
            status.State == FileSyncState.Synced ? MessageBoxImage.Information : MessageBoxImage.None);
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
    public void AddComputer() => Dialogs.Ask(null, Relay.AddComputerSteps(_settings.ServerUrl, _settings.AccountEmail, _session?.Status.NestUrl ?? NestFromSettings()),
        "Pairnets – add a computer", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>The nest's Devices page, or the account page when this computer signed in with a Pairnets account.</summary>
    public void ManageDevices()
    {
        if (Relay.ManageComputersUrl(_settings.ServerUrl, _session?.Status.NestUrl ?? NestFromSettings()) is { } url)
            Shell(url);
        else
            Dialogs.Ask(null, "Your nest has no website yet. On the server, give it its own name with: sudo ./install.sh --public-url https://nest.example.com",
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
        if (_signingOut)
            return; // a second click while the first is still working
        _signingOut = true;
        try
        {
            if (Dialogs.Ask(null, $"Sign out of this computer?\n\nSyncing stops. Your files in {_settings.Folder} stay here. Sign in again to continue.",
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
            _log.LogInformation("Signed out of this computer by the person (Sign out); the nest was {Told}", removed ? "told" : "not told");
            _settings.ProtectedToken = null;
            _settings.DeviceId = null;
            _settings.FirstRunCompleted = false;
            SettingsStore.Save(_env.SettingsPath, _settings);
            _dirty = true;
            if (!removed)
                Dialogs.Ask(null, "Signed out here, but your nest could not be told. " + Relay.RemoveByHandHint(_settings.ServerUrl),
                    "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
            _window?.Navigate(MainPage.Overview);
        }
        finally
        {
            _signingOut = false;
        }
        ShowSettings(firstRun: true);
    }

    private bool _signingOut;

    /// <summary>One-time changes for settings an older Pairnets saved (<see cref="SettingsUpgrades"/>), saved at once.</summary>
    private void UpgradeSettings()
    {
        var done = SettingsUpgrades.Apply(_settings);
        if (done.Count == 0 || !File.Exists(_env.SettingsPath))
            return; // nothing changed, or nothing set up yet (the first run saves them)
        foreach (var line in done)
            _log.LogInformation("Settings: {Change}", line);
        try
        {
            SettingsStore.Save(_env.SettingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save the upgraded settings: {Error}", ex.Message);
        }
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
        if (Dialogs.Ask(null, $"Reset Pairnets on this computer?\n\nThis signs this computer out of your nest, forgets every setting and the saved sign-in, and removes Pairnets' sync notes. Your files in {where} are not deleted. The log files are kept.\n\nYou start again from the sign-in screen.",
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
            var leftExplorer = false;
            var stayedOnNest = 0;
            try
            {
                stayedOnNest = _platform.ForgetCloudFolders();
                leftExplorer = _platform.SetExplorerEntry(null);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not take Pairnets out of File Explorer");
            }
            _log.LogInformation("Reset this app chosen by the person: forgetting the settings, the saved key and the sync notes");
            var result = LocalReset.Run(_settings, _platform.Secrets, _env.SettingsPath, _env.LocalDir);
            var notes = new List<string>(result.Problems);
            if (stayedOnNest > 0)
                notes.Add($"{stayedOnNest} file(s) were only on your nest; they stay in the folder as shortcuts that open once Pairnets is set up again.");
            if (!leftExplorer)
                notes.Add("Pairnets could not be taken out of File Explorer completely.");
            try
            {
                _platform.SetAutoStart(false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                notes.Add("Start with Windows could not be turned off: " + FriendlyErrors.Describe(ex));
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
                Dialogs.Ask(null, "Pairnets was reset, with these notes:\n\n• " + string.Join("\n• ", notes), "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            next.ProtectedToken = _platform.Secrets.Protect(token);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not protect this computer's new key: {Error}", ex.Message);
            return;
        }
        next.NameOfferAnswered |= _settings.NameOfferAnswered; // answered after this session started
        _log.LogInformation("Saving this computer's sign-in: {Server} as {Device} ({Kind})", next.ServerUrl, next.DeviceName, next.HasOwnKey ? "own key" : "shared token");
        ApplySettings(next, token);
    }

    /// <summary>"Use StudyPC" on the name offer: renames this computer on its nest; the session then saves the new name.</summary>
    private async void UseOwnName(NameOffer offer)
    {
        if (_session is not { } session)
            return;
        try
        {
            await session.RenameAsync(offer.Own);
            AnswerNameOffer();
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            Dialogs.Ask(null, "Pairnets could not rename this computer. " + FriendlyErrors.Describe(ex), "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Either answer to the name offer: it is not offered again.</summary>
    private void AnswerNameOffer()
    {
        _settings.NameOfferAnswered = true;
        SettingsStore.Save(_env.SettingsPath, _settings);
        _dirty = true;
    }

    private void StopSession()
    {
        if (_session is null)
            return;
        var s = _session;
        _session = null;
        _leaving?.Cancel();
        try
        {
            // A folder being turned back into a plain one finishes first, so a new start never meets it half way.
            _leaveTask?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // (it says what happened itself)
        }
        _leaveTask = null;
        Task.Run(async () => await s.DisposeAsync()).Wait(TimeSpan.FromSeconds(30));
        _cloud?.Dispose(); // after the session: nothing asks it any more
        _cloud = null;
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
        var status = _session?.Status ?? StatusSnapshot.WithoutSession(_settings);

        if (_shownStatus != status.IconStatus)
        {
            _icon.Icon = TrayIcons.For(status.IconStatus);
            _shownStatus = status.IconStatus;
        }
        // What it is doing in one line: "Checking files · 12,000 of 38,206", "Uploading · 37 of 120 files · 4.90 MB/s · …".
        var line = status.StatusLine;
        var tip = $"Pairnets: {line} ({status.LastSyncText.ToLowerInvariant()})";
        _icon.Text = tip.Length > 127 ? tip[..124] + "..." : tip;
        _statusItem.Text = line.Length > 80 ? line[..77] + "..." : line;
        _fixProblem.Visible = status.FixLabel is not null;
        _fixProblem.Text = status.FixLabel ?? "Fix…";
        _pause.Text = status.Paused ? "Resume syncing" : "Pause syncing";

        if (_window is { IsVisible: true })
        {
            DrawUpdateBanner();
            _window.ShowMapSpeed = _settings.ShowMapSpeed;
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
        if (NameOffer.For(_settings, ComputerName.Default()) is { } offer)
            items.Add(new AttentionItem(offer.Title, offer.Detail, offer.UseLabel, () => UseOwnName(offer), AttentionKind.Rename,
                SecondLabel: offer.KeepLabel, Second: AnswerNameOffer));
        foreach (var join in status.JoinRequests)
        {
            items.Add(new AttentionItem(join.Title, join.Advice,
                "Review in browser", () => ReviewJoin(join), AttentionKind.Join, join.Code, join.At));
        }
        if (status.FixLabel is not null)
            items.Add(new AttentionItem("Syncing is paused until you decide", _session.Status.Text, status.FixLabel, FixBlocked));
        foreach (var w in _session.Warnings())
            items.Add(new AttentionItem(w.Path, w.Message ?? w.Code, "Show in folder", () => RevealFile(w.Path), w.Code == ErrorCodes.CaseCollision ? AttentionKind.NameClash : AttentionKind.Warning));
        foreach (var c in _session.Activity.Items.Where(i => i.Kind == Pairnets.Core.Client.ActivityKind.Conflict && i.Path is not null).Take(20))
        {
            if (File.Exists(LocalPath(c.Path!)))
                items.Add(new AttentionItem("Conflict copy: " + PathRules.FileName(c.Path!), "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => RevealFile(c.Path!), AttentionKind.Conflict, null, c.Time));
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
        if (!NotifyPolicy.Allows(_settings, key))
            return; // turned off in the notification switches
        var now = DateTime.UtcNow;
        if (_lastToast.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10))
            return;
        _lastToast[key] = now;
        _balloonClick = onClick;
        _icon.ShowBalloonTip(8000, title, text, icon);
        Notified?.Invoke(title, text);
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
        if (_window is null)
        {
            _window = new MainWindow(this) { SideFolded = _settings.SideFolded };
            _window.SideFoldedChanged += folded =>
            {
                _settings.SideFolded = folded;
                SettingsStore.Save(_env.SettingsPath, _settings);
            };
        }
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

    public INestFiles? Files => _session?.Files;

    public void OpenFile(string path) => Shell(path);

    public bool Notifies(NoticeKind kind) => kind switch
    {
        NoticeKind.JoinRequest => _settings.NotifyJoinRequests,
        NoticeKind.Attention => _settings.NotifyAttention,
        NoticeKind.Update => _settings.NotifyUpdates,
        _ => true,
    };

    public void SetNotifies(NoticeKind kind, bool on)
    {
        switch (kind)
        {
            case NoticeKind.JoinRequest: _settings.NotifyJoinRequests = on; break;
            case NoticeKind.Attention: _settings.NotifyAttention = on; break;
            case NoticeKind.Update: _settings.NotifyUpdates = on; break;
            default: return;
        }
        SettingsStore.Save(_env.SettingsPath, _settings);
    }

    public void OpenAccountSite() => Shell(Relay.DefaultServiceUrl.ToString());

    /// <summary>The Devices page was opened: ask the server for the current list.</summary>
    public void RefreshDevices()
    {
        if (_session is { } session)
            _ = session.RefreshDevicesAsync();
    }

    /// <summary>Saves the icon chosen for a computer on the nest; false when the nest cannot (too old, or unreachable).</summary>
    public Task<bool> SetDeviceIcon(string id, string icon) =>
        _session is { } session ? session.SetDeviceIconAsync(id, icon) : Task.FromResult(false);

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
        _panel.ShowStatus(_session?.Status ?? StatusSnapshot.WithoutSession(_settings), _settings.DeviceName);
        _panel.ShowActivity(_session?.Activity.Items ?? []);
        _panel.ShowAttention(BuildAttention(_session?.Status ?? StatusSnapshot.WithoutSession(_settings)).Count);
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
        SettingsStore.Save(_env.SettingsPath, _settings);
    }

    public void FixBlocked()
    {
        if (_session is null)
        {
            // Signed out here: the Overview's and the tray menu's "Sign in…" open the sign-in window.
            if (StatusSnapshot.WithoutSession(_settings).BlockReason == BlockReason.SignedOutByYou)
                ShowSettings(firstRun: true);
            return;
        }
        var status = _session.Status;
        switch (status.BlockReason)
        {
            case BlockReason.MassDelete:
            case BlockReason.FolderEmpty:
                if (_session.PendingDeletes().Count is > 0 and var pending
                    && Dialogs.Confirm(_window is { IsVisible: true } ? _window : null, _session.DescribePendingDeletes(), "Pairnets – allow deletions?",
                        OverviewModel.DeleteButtons(pending).Yes, OverviewModel.DeleteButtons(pending).No))
                    _session.ApproveDeletions();
                break;
            case BlockReason.ForeignMarker:
                if (Dialogs.Ask(null, $"{_settings.Folder} was synced by Pairnets before, but this computer has no record of it (for example after reinstalling).\n\n" +
                        "Continue syncing it? Files are merged: nothing is deleted, differing files become conflict copies.",
                        "Pairnets", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _session.AdoptExistingMarker();
                break;
            case BlockReason.ServerChanged:
            case BlockReason.ServerRolledBack:
                if (Dialogs.Ask(null, status.Text + "\n\nRe-link? Pairnets will merge this folder with the server: nothing is deleted or overwritten, " +
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
        Dialogs.Ask(null, message + "\n\nIf the drive is unplugged, plug it in and choose Sync now. If you moved or renamed the folder, choose its new location next.",
            "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (Dialogs.PickFolder(null, "Where is your Pairnets folder now?") is not { } folder || _session is null)
            return;
        var error = _session.CheckMovedFolder(folder);
        if (error is not null)
        {
            Dialogs.Ask(null, error, "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var oldStateDir = _session.StateDirectory;
        StopSession();
        try
        {
            StateLocator.AdoptState(oldStateDir, folder, _env.LocalDir);
        }
        catch (IOException ex)
        {
            Dialogs.Ask(null, FriendlyErrors.Describe(ex), "Pairnets", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _settings.Folder = folder;
        SettingsStore.Save(_env.SettingsPath, _settings);
        StartSession(null);
    }

    private SettingsWindow? _signInWindow;

    /// <summary>Signing in is a window of its own; later, Settings is a page of the main window.</summary>
    public void ShowSettings(bool firstRun)
    {
        if (!firstRun && _settings.IsComplete && _settings.FirstRunCompleted)
        {
            ShowMainWindow(MainPage.Settings);
            return;
        }
        // A second tray click or toast while the sign-in is open brings it forward instead of opening another.
        if (_signInWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new SettingsWindow(_settings, _platform.Secrets, Shell, _session?.Status.NestUrl);
        _signInWindow = window;
        bool? accepted;
        try
        {
            accepted = window.ShowDialog();
        }
        finally
        {
            _signInWindow = null;
        }
        if (accepted != true || window.Result is null)
            return;
        ApplySettings(window.Result, window.PlainToken);
        ShowMainWindow();
    }

    private void ShowSettingsPage()
    {
        if (_window is null)
            return;
        var view = new SettingsView(_settings, _platform.Secrets, _updates, _session?.Status.ServerVersionText, _session?.Files);
        view.OpenFilesRequested += () => _window?.Navigate(MainPage.Files);
        view.Saved += v =>
        {
            ApplySettings(v.Result!, v.PlainToken);
            _window?.Navigate(MainPage.Overview);
        };
        view.SwitchesChanged += ApplySwitches;
        view.ResetRequested += ResetEverything;
        view.ShowDevicesRequested += () => _window?.Navigate(MainPage.Devices);
        view.ReportBugRequested += () => ReportBug();
        view.OpenLinkRequested += Shell;
        view.Cancelled += () => _window?.Navigate(MainPage.Overview);
        view.ChangeStorageRequested += () => ShowNestPlace(view);
        _window.ShowSettingsPage(view);
        _ = FillNestStorageAsync(view);
    }

    /// <summary>What the nest last said about where it keeps its files (the window shows it at once).</summary>
    private NestStorage? _nestStorage;

    /// <summary>Settings' "Where your nest keeps files" row, for a nest that can move its files (asks it once).</summary>
    private async Task FillNestStorageAsync(SettingsView view)
    {
        if (_session is not { } session || session.Status.Server is not { CanMoveStorage: true })
            return;
        try
        {
            _nestStorage = await session.Api.GetStorageAsync(CancellationToken.None);
            view.ShowNestStorage(_nestStorage);
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsProtocolException or PairnetsAuthException or HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug("The nest's storage could not be read: {Error}", ex.Message);
        }
    }

    /// <summary>"Change…" on that row: the nest's drives and the move; the row is read again when it closes.</summary>
    private void ShowNestPlace(SettingsView view)
    {
        var window = new NestPlaceWindow(() => _session, _nestStorage);
        window.Closed += (_, _) => _ = FillNestStorageAsync(view);
        window.Show();
    }

    /// <summary>
    /// A switch on the Settings page changed: it applies at once (no Save). Most only need saving; the speed limits change
    /// on the running connection, the file manager entry follows its switch, and the engine settings start the session
    /// again (<see cref="SettingsSwitches"/>).
    /// </summary>
    private void ApplySwitches(ClientSettings fromPage)
    {
        var before = _settings;
        var next = SettingsSwitches.WithSwitchesFrom(_settings, fromPage);
        _settings = next;
        _fileLog.Minimum = _settings.DebugMode ? LogLevel.Debug : LogLevel.Information;
        SettingsStore.Save(_env.SettingsPath, _settings);
        if (before.StartWithWindows != next.StartWithWindows)
            SetAutoStart(next.StartWithWindows);
        if (before.CheckForUpdates != next.CheckForUpdates)
            _updates.SetEnabled(next.CheckForUpdates);
        if (_session is { } session)
        {
            if (SettingsSwitches.NeedsNewSession(before, next))
                StartSession(null); // also puts Pairnets in the file manager, or takes it away
            else
            {
                if (SettingsSwitches.LimitsChanged(before, next))
                {
                    session.Api.UploadLimit.SetMegabytesPerSecond(next.UploadLimitMBps);
                    session.Api.DownloadLimit.SetMegabytesPerSecond(next.DownloadLimitMBps);
                }
                if (SettingsSwitches.FileManagerChanged(before, next))
                    UpdateExplorerEntry();
            }
        }
        _dirty = true;
    }

    /// <summary>
    /// What was ticked on the installer's "Select Additional Tasks" page, taken into the settings once (a later silent
    /// update leaves nothing, so changes made in Settings since are kept).
    /// </summary>
    private void TakeSetupChoices()
    {
        if (_platform.ReadSetupChoices() is not { } setup || setup.Id == _settings.SetupChoicesApplied)
            return;
        SetupChoices.Apply(_settings, setup.Choices);
        _settings.SetupChoicesApplied = setup.Id;
        try
        {
            SettingsStore.Save(_env.SettingsPath, _settings);
            _platform.SetAutoStart(_settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.LogWarning("Could not keep the installer's choices: {Error}", ex.Message);
        }
        _log.LogInformation("Took the installer's choices: {Choices}", setup.Choices);
    }

    private void ApplySettings(ClientSettings settings, string? plainToken)
    {
        _settings = settings;
        _fileLog.Minimum = _settings.DebugMode ? LogLevel.Debug : LogLevel.Information;
        SettingsStore.Save(_env.SettingsPath, _settings);
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
        // A failed update says why in one line; its button is the next step (none for a release Pairnets did not sign),
        // and the X puts the card away.
        if (!update.Signed)
            _window.ShowUpdate($"Pairnets {update.Version} could not be installed", UpdateProblem.NotSigned.Reason, UpdateProblem.NotSigned.ButtonText);
        else if (_updateError is { } problem)
            _window.ShowUpdate($"Pairnets {update.Version} could not be installed", problem.Reason, problem.ButtonText);
        else if (_updateProgress is { } p)
            _window.ShowUpdate($"Downloading Pairnets {update.Version}…", "Syncing continues while it downloads.", UpdateTexts.UpdateNow, p, busy: true);
        else
            _window.ShowUpdate($"Pairnets {update.Version} is available", UpdateTexts.Ready(PairnetsInfo.ProductVersion), UpdateTexts.UpdateNow);
    }

    /// <summary>True when this Pairnets.exe was put there by PairnetsSetup.exe (so the installer can replace it).</summary>
    private bool IsInstalledCopy
    {
        get
        {
            var installDir = _env.InstallDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pairnets");
            return string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.TrimEndingDirectorySeparator(installDir),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Downloads the update and checks it against the signed SHA256SUMS.txt. An installed copy runs PairnetsSetup.exe
    /// hidden (it closes this Pairnets, replaces it and starts the new one; no installer window shows). A copy started from the zip swaps its own
    /// Pairnets.exe for the one in pairnets-client-win-x64.zip and starts it (<see cref="SelfReplace"/>). When anything
    /// fails, the card says why and offers to try again; it never sends the person to the browser instead.
    /// </summary>
    public async void UpdateNow()
    {
        if (_updates.Available is not { } update || _updateProgress is not null)
            return;
        if (!update.Signed)
        {
            DismissUpdate(); // never point people at a release Pairnets did not sign
            return;
        }
        var download = Path.Combine(Path.GetTempPath(), _package.Method == UpdateMethod.Installer
            ? $"PairnetsSetup-{update.Version}.exe"
            : $"Pairnets-{update.Version}-{update.AssetName}");
        _updateError = null;
        _updateProgress = 0;
        _dirty = true;
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                _updateProgress = p.Total is > 0 ? p.Done * 100.0 / p.Total.Value : 0;
                _dirty = true;
            });
            await _env.CreateUpdateChecker().DownloadVerifiedAsync(update, download, progress, CancellationToken.None);
            if (_package.Method == UpdateMethod.Installer)
            {
                _log.LogInformation("Installing Pairnets {Version}", update.Version);
                _platform.RunInstaller(download, UpdatePackage.InstallerArguments);
            }
            else
            {
                var app = _env.AppFile ?? throw new IOException("Pairnets could not find its own file.");
                _log.LogInformation("Replacing {App} with Pairnets {Version}", app, update.Version);
                try
                {
                    await Task.Run(() => SelfReplace.Install(download, _package.AppFile!, app));
                }
                finally
                {
                    File.Delete(download);
                }
                if (!StartUpdatedApp(app, update.Version))
                    return;
            }
            Exit();
        }
        catch (Exception ex) when (ex is UpdateVerificationException or HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            _log.LogWarning("Update failed: {Error}", FriendlyErrors.Technical(ex));
            _updateError = UpdateProblem.From(ex, _env.AppFile);
            _updateProgress = null;
            _dirty = true;
        }
    }

    /// <summary>Starts the new Pairnets.exe that just replaced this one; false (and the card says so) when Windows would not.</summary>
    private bool StartUpdatedApp(string app, string version)
    {
        try
        {
            _platform.StartUpdatedApp(app, SelfReplace.AfterUpdateArgument);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.LogWarning("Could not start the new Pairnets: {Error}", ex.Message);
            _updateError = new UpdateProblem($"Pairnets {version} is installed, but it couldn't be started. Exit Pairnets and start it again to use the new version.", UpdateNextStep.TryAgain);
            _updateProgress = null;
            _dirty = true;
            return false;
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
            Toast("disk", "Your nest is running out of space", $"{session.Status.ServerFreeText}. Free up space on your nest or delete old history.", Forms.ToolTipIcon.Warning, ShowMainWindow);
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
                SettingsStore.Save(_env.SettingsPath, _settings);
        };
        dialog.Show();
    }

    public void OpenFolder()
    {
        if (_settings.Folder is { } f && Directory.Exists(f))
            Shell(f);
    }

    /// <summary>"Sign in again" on the Account page: the sign-in window.</summary>
    void IMainActions.SignInAgain() => ShowSettings(firstRun: true);

    void IMainActions.ShowSettings()
    {
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
            ShowSettings(firstRun: true);
        else
            ShowSettingsPage();
    }

    public void ViewLog() => Shell(_fileLog.CurrentFile);

    /// <summary>"Report a bug": a report to paste to whoever helps (copied and saved; only crash cards are sent, and only on "Send").</summary>
    public void ReportBug() => ShowBugReport(null);

    /// <summary>Offered after an unexpected error: the same report, with that error in it.</summary>
    public void ReportBug(Exception error) => ShowBugReport(error);

    /// <summary>The names to take out of a crash card: this computer's and this app's (<see cref="PrivacyCleaner.ThisComputer(ClientSettings)"/>).</summary>
    public IEnumerable<string?> PrivateValues() => PrivacyCleaner.ThisComputer(_settings);

    private void ShowBugReport(Exception? error)
    {
        var settings = _settings;
        var session = _session;
        var crashes = _env.CrashesDir;
        var window = new BugReportWindow(() => BugReport.BuildAsync(settings, session, _fileLog.CurrentFile, error, AppName, crashes), Shell,
            afterError: error is not null, logsDir: _env.LogsDir, crashesDir: crashes,
            sendCrashes: (cards, note) => CrashSender.SendAsync(crashes, cards, note, PrivacyCleaner.ThisComputer(settings)));
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
                _platform.Reveal(full);
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
            _platform.SetAutoStart(enabled);
            _settings.StartWithWindows = enabled;
            SettingsStore.Save(_env.SettingsPath, _settings);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Dialogs.Ask(null, "Could not change the startup setting: " + FriendlyErrors.Describe(ex), "Pairnets");
        }
    }

    private bool SafeIsAutoStart()
    {
        try
        {
            return _platform.IsAutoStartEnabled();
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
            _platform.Open(path);
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
        _shutdown();
    }

    public void Dispose()
    {
        _activation?.Dispose();
        _updates.Dispose();
        _refresh.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopSession();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
