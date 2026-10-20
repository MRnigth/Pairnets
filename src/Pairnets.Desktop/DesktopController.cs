using Pairnets.Core.Api;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Logging;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Platform;
using Pairnets.Desktop.Views;

namespace Pairnets.Desktop;

/// <summary>
/// What the controller needs from the desktop lifetime: the open windows, and quitting. Avalonia's own lifetime
/// interface cannot be implemented outside Avalonia, so the tests hand in a desktop of their own through this.
/// </summary>
internal interface IDesktopLifetime
{
    IReadOnlyList<Window> Windows { get; }

    void Shutdown();
}

/// <summary>
/// Menu-bar (macOS) / system-tray (Linux) icon, main window and notifications around a
/// <see cref="ClientSession"/>. Mirrors the Windows TrayController; all sync logic is in Pairnets.Core.
/// </summary>
public sealed class DesktopController : ITrayActions, IDisposable
{
    private readonly Application _app;
    private readonly IDesktopLifetime _lifetime;
    private readonly IPlatformServices _platform;
    private readonly ClientEnvironment _env;
    private readonly RollingFileLoggerProvider _fileLog;
    private bool _reportingError;
    private readonly CrashCatcher _crashes;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Dictionary<RunnerStatus, WindowIcon> _icons = [];
    private readonly Dictionary<string, DateTime> _lastNotice = [];
    private readonly DispatcherTimer _refresh;
    private ClientSettings _settings;
    private ClientSession? _session;
    private MainWindow? _window;
    private TrayPanel? _panel;
    private TrayIcon? _tray;
    private NativeMenu? _trayMenu;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _fixItem;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _autoStartItem;
    private volatile bool _dirty = true;
    private RunnerStatus? _shownStatus;
    private readonly UpdateService _updates;
    private readonly UpdatePackage _package;
    private bool _updateDismissed;
    private double? _updateProgress;
    private UpdateProblem? _updateError;
    private string? _serverPromptShownFor;
    private bool _lowSpaceNoticed;
    private IDisposable? _activation;

    /// <param name="environment">Where the settings, sync notes and logs live and where updates come from
    /// (<see cref="ClientEnvironment.Default"/> unless a test gives the app folders of its own).</param>
    public DesktopController(Application app, IClassicDesktopStyleApplicationLifetime lifetime, IPlatformServices platform, ClientEnvironment? environment = null)
        : this(app, new AvaloniaDesktop(lifetime), platform, environment)
    {
    }

    internal DesktopController(Application app, IDesktopLifetime lifetime, IPlatformServices platform, ClientEnvironment? environment)
    {
        _app = app;
        _lifetime = lifetime;
        _platform = platform;
        _env = environment ?? ClientEnvironment.Default;
        _fileLog = new RollingFileLoggerProvider(_env.LogsDir, retentionDays: 14);
        _loggers = LoggerFactory.Create(b => b.AddProvider(_fileLog).SetMinimumLevel(LogLevel.Debug));
        _log = _loggers.CreateLogger("Pairnets.Desktop");
        _settings = SettingsStore.Load(_env.SettingsPath);
        _crashes = new CrashCatcher(_env.CrashesDir, AppName, _log, () => PrivacyCleaner.ThisComputer(_settings));
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        _fileLog.Minimum = _settings.DebugMode ? LogLevel.Debug : LogLevel.Information; // Debug mode: more detail in the log
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshIfDirty());
        _package = UpdatePackage.ForThisComputer(installedCopy: false); // (no installer looks after the Mac and Linux app)
        _updates = new UpdateService(_env.CreateUpdateChecker(), PairnetsInfo.ProductVersion, _package.Asset);
        _updates.UpdateAvailable += u =>
        {
            _updateDismissed = false;
            _updateError = null;
            _dirty = true;
            if (!u.Signed)
                return; // (the banner says it was not installed; no "update available" notice for it)
            Notify("update:" + u.Version, "Pairnets update available", $"Version {u.Version} is ready. Open Pairnets to update.");
        };
    }

    /// <summary>How reports and crash cards name this app.</summary>
    public static string AppName => OperatingSystem.IsMacOS() ? "Mac app" : "Linux app";

    /// <summary>Also catches errors behind the scenes and in background tasks, for the whole app (the app does this; tests do not).</summary>
    public void WatchForCrashes() => _crashes.Watch();

    public void Start(string[] args)
    {
        _log.LogInformation("Pairnets {Version} starting on {Platform}", typeof(DesktopController).Assembly.GetName().Version, _platform.Name);
        // A pairnets:// link (the nest's website after approving this computer) starts a second
        // Pairnets, which pokes this one through the pipe and quits; come to the front for it, or do what a
        // right-click item in the file manager asked. The poke is answered once that is done.
        _activation = AppActivation.Listen(message => Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (FileManagerCommand.FromMessage(message) is { } command)
                HandleFileCommand(command);
            else
                ComeToFront();
        }).GetTask(), _env.ActivationPipeName);
        CreateTray();
        _refresh.Start();
        _updates.SetEnabled(_settings.CheckForUpdates);
        // Pairnets had to close after an error since it last ran: say so once (the card is in "Report a bug").
        if (CrashCards.TakeNewCloses(_env.CrashesDir).Count > 0)
            Notify("crash", CrashNotice.Title, CrashNotice.Text);
        // Rewrite the login item so it follows the app (Tether.app became Pairnets.app; the app may have moved).
        if (SafeIsAutoStart())
        {
            try
            {
                _platform.SetAutoStart(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Could not refresh start-at-login: {Error}", ex.Message);
            }
        }
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSignIn();
            return;
        }
        StartSession(null);
        if (FileManagerCommand.FromArguments(args) is { } command)
            HandleFileCommand(command); // started by a right-click item in the file manager while Pairnets was not running
        else if (!args.Contains(AppActivation.AutoStartArgument))
            ShowMainWindow();
    }

    // ------------------------------------------------------------------ tray / menu bar

    private void CreateTray()
    {
        var menu = new NativeMenu();
        var quick = new NativeMenuItem("Quick status…");
        quick.Click += (_, _) => TogglePanel();
        var open = new NativeMenuItem("Open Pairnets");
        open.Click += (_, _) => ShowMainWindow();
        _statusItem = new NativeMenuItem("Starting…") { IsEnabled = false };
        var sync = new NativeMenuItem("Sync now");
        sync.Click += (_, _) => SyncNow();
        var folder = new NativeMenuItem("Open folder");
        folder.Click += (_, _) => OpenFolder();
        _fixItem = new NativeMenuItem("No action needed") { IsEnabled = false };
        _fixItem.Click += (_, _) => FixBlocked();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings();
        var log = new NativeMenuItem("View log");
        log.Click += (_, _) => ViewLog();
        var report = new NativeMenuItem("Report a bug…");
        report.Click += (_, _) => ReportBug();
        _pauseItem = new NativeMenuItem("Pause syncing");
        _pauseItem.Click += (_, _) => TogglePause();
        _autoStartItem = new NativeMenuItem("Start at login") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = SafeIsAutoStart() };
        _autoStartItem.Click += (_, _) => SetAutoStart(!SafeIsAutoStart());
        var quit = new NativeMenuItem("Quit Pairnets");
        quit.Click += (_, _) => Quit();
        foreach (var item in new NativeMenuItemBase[] { quick, open, _statusItem, new NativeMenuItemSeparator(), sync, folder, _fixItem,
                     new NativeMenuItemSeparator(), settings, log, report, _pauseItem, _autoStartItem, new NativeMenuItemSeparator(), quit })
            menu.Items.Add(item);
        _trayMenu = menu;
        if (!_env.ShowTrayIcon)
            return; // tests: the menu works the same, but no icon appears in the real menu bar or tray

        _tray = new TrayIcon
        {
            Icon = IconFor(RunnerStatus.Offline),
            ToolTipText = "Pairnets",
            Menu = menu,
            IsVisible = true,
        };
        // A click opens the quick-look panel where the desktop reports clicks (most Linux trays;
        // the macOS menu bar always shows the menu, which starts with "Quick status…").
        _tray.Clicked += OnTrayClicked;
        TrayIcon.SetIcons(_app, [_tray]);
    }

    private void OnTrayClicked(object? sender, EventArgs e) => TogglePanel();

    private void TogglePanel()
    {
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSignIn();
            return;
        }
        _panel ??= new TrayPanel(this);
        if (_panel.IsVisible || DateTime.UtcNow - _panel.HiddenAt < TimeSpan.FromMilliseconds(300))
        {
            _panel.Hide(); // a second click on the icon closes it
            return;
        }
        if (_session is { } session)
            _ = session.RefreshDevicesAsync();
        _dirty = true;
        RefreshIfDirty();
        _panel.Open();
        _dirty = true;
        RefreshIfDirty();
    }

    public void OpenWindow(MainPage page) => ShowMainWindow(page);

    /// <summary>White reads on every status colour but the yellow of "paused": that one gets a dark brown sign (7:1).</summary>
    private static readonly IBrush PausedInk = new SolidColorBrush(Color.Parse("#2B1A00"));

    private WindowIcon IconFor(RunnerStatus status)
    {
        if (_icons.TryGetValue(status, out var icon))
            return icon;
        var (brushKey, iconKey) = Visuals.ForStatus(status);
        try
        {
            var fill = Visuals.Resource<IBrush>(brushKey) ?? Brushes.Gray;
            var glyph = Visuals.Resource<Geometry>(iconKey);
            var bitmap = new RenderTargetBitmap(new PixelSize(44, 44), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                ctx.DrawEllipse(fill, null, new Point(22, 22), 21, 21);
                if (glyph is not null)
                {
                    // 24-unit icon scaled into the middle 26 px of the circle.
                    using (ctx.PushTransform(Matrix.CreateScale(26 / 24.0, 26 / 24.0) * Matrix.CreateTranslation(9, 9)))
                        ctx.DrawGeometry(null, new Pen(status == RunnerStatus.Paused ? PausedInk : Brushes.White, 3.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), glyph);
                }
            }
            using var ms = new MemoryStream();
            bitmap.Save(ms);
            ms.Position = 0;
            icon = new WindowIcon(ms);
        }
        catch (Exception ex)
        {
            _log.LogDebug("Could not draw status icon: {Error}", ex.Message);
            icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Pairnets/Assets/pairnets-icon-256.png")));
        }
        _icons[status] = icon;
        return icon;
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
            _log.LogWarning("Cannot read the saved token: {Error}", ex.Message);
            Notify("token", "Pairnets needs the token again", "The saved token cannot be read. Open Settings and enter it.");
            return;
        }

        var session = ClientSession.Start(_settings, plainToken, _platform.CreateTrash(_loggers.CreateLogger("Pairnets.Trash")), _loggers, stateBaseDir: _env.LocalDir);
        session.StatusChanged += _ => _dirty = true;
        session.Activity.Added += _ => _dirty = true;
        session.ConflictCreated += c => Notify("conflict:" + c.Path, "Conflict: both computers changed a file",
            $"{c.Path}: your version was kept as \"{PathRules.FileName(c.ConflictCopyPath)}\".");
        session.PathWarningRaised += w => Notify("warning:" + w.Path, w.Code == ErrorCodes.CaseCollision ? "Name collision" : "File name not allowed", $"{w.Path}: {w.Message}");
        session.PassCompleted += r =>
        {
            if (r.Outcome == PassOutcome.Blocked)
                Notify("blocked:" + r.BlockReason, r.BlockReason is BlockReason.MassDelete or BlockReason.FolderEmpty ? "Deletions blocked"
                    : r.BlockReason == BlockReason.SignedOut ? "Signed out of your nest"
                    : r.BlockReason == BlockReason.SignInRequired ? "Sign in to your nest" : "Pairnets paused syncing", r.Message ?? r.BlockReason.ToString());
            else if (r.Outcome == PassOutcome.AuthFailed)
                Notify("auth", "Sign in to your nest", "Your nest did not accept this computer's sign-in. Open Pairnets and sign in again to keep syncing.");
        };
        session.ServerInfoChanged += info => Dispatcher.UIThread.Post(() => OnServerInfo(session, info));
        session.AccountChanged += (next, token) => Dispatcher.UIThread.Post(() => OnAccountChanged(session, next, token));
        session.Files.Opened.NestFullWhileSaving += path => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_session, session) && _window is { } w)
                _ = w.ShowNestFullAsync(); // a file opened from the nest could not be saved back
        });
        session.JoinRequested += r => Notify("join:" + r.Code, r.Title, $"Code {r.Code}. Review it on your nest (Pairnets → Needs attention).");
        session.CatchUpCompleted += n => Notify("catchup", "Pairnets is up to date", $"Synced {n} change(s) made while this computer was away.");
        _session = session;
        _dirty = true;
        _log.LogInformation("Syncing {Folder} with {Server} as {Device}", _settings.Folder, _settings.ServerUrl, _settings.DeviceName);
        UpdateFileManagerEntry();
    }

    // ------------------------------------------------------------------ the file manager

    /// <summary>
    /// Puts Pairnets in the file manager for the current folder (Linux: sidebar bookmark, right-click items, folder
    /// icon; Mac: folder icon), or takes it away when switched off in Settings. Runs at every start.
    /// </summary>
    private void UpdateFileManagerEntry()
    {
        var wanted = _settings.ShowInFileManager || (_settings.ExplorerRightClick && !OperatingSystem.IsMacOS());
        var folder = wanted && _settings.IsComplete && Directory.Exists(_settings.Folder) ? _settings.Folder : null;
        try
        {
            if (!_platform.SetFileManagerEntry(folder, sidebar: _settings.ShowInFileManager, rightClick: _settings.ExplorerRightClick))
                _log.LogInformation("Pairnets could not be fully added to the file manager; it tries again at the next start");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not update Pairnets in the file manager");
        }
    }

    /// <summary>A right-click item in the file manager: "Open in Pairnets" or "Show sync status" for a file or folder.</summary>
    internal async void HandleFileCommand(FileManagerCommand command)
    {
        try
        {
            await HandleFileCommandAsync(command);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not answer the file manager about {Path}", command.Path);
        }
    }

    private async Task HandleFileCommandAsync(FileManagerCommand command)
    {
        if (_session is not { } session || _settings.Folder is not { } folder || FileStatusReader.ToSyncPath(folder, command.Path) is not { } rel)
        {
            ShowMainWindow();
            return;
        }
        if (command.Action == FileManagerAction.WhereItLives)
        {
            // A folder's own choice; for a file, the folder it is in.
            var folderPath = rel.Length == 0 || Directory.Exists(command.Path) ? rel : Pairnets.Core.Paths.PathRules.Parent(rel) ?? string.Empty;
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
                    w.SearchHistory(rel);
            }
            else
            {
                ShowMainWindow();
                _window?.ShowVersionsFor(rel);
            }
            return;
        }
        var status = session.GetFileStatus(command.Path);
        var name = rel.Length == 0 ? "Your Pairnets folder" : Pairnets.Core.Paths.PathRules.FileName(rel);
        if (status.State == FileSyncState.Problem)
        {
            if (await Dialogs.ConfirmAsync(_window, "Pairnets – sync status", $"{name}\n\n{status.Text}\n\nOpen Pairnets to see what needs you?"))
                ShowMainWindow(MainPage.Attention);
            return;
        }
        await Dialogs.InfoAsync(_window, "Pairnets – sync status", $"{name}\n\n{status.Text}");
    }

    /// <summary>Opens the nest's page where a computer asking to join is allowed or turned away.</summary>
    private void ReviewJoin(JoinRequest request)
    {
        if (_session?.Status.ReviewUrl(request) is { } url)
            _platform.Open(url);
        else
            ShowMainWindow(MainPage.Attention);
    }

    // ------------------------------------------------------------------ the nest: devices and signing out

    /// <summary>The two steps, with this nest's name (or, signed in with an account, the account) filled in.</summary>
    public async void AddComputer() => await Dialogs.InfoAsync(_window, "Pairnets – add a computer",
        Relay.AddComputerSteps(_settings.ServerUrl, _settings.AccountEmail, _session?.Status.NestUrl ?? NestFromSettings()));

    /// <summary>The nest's Devices page, or the account page when this computer signed in with a Pairnets account.</summary>
    public async void ManageDevices()
    {
        if (Relay.ManageComputersUrl(_settings.ServerUrl, _session?.Status.NestUrl ?? NestFromSettings()) is { } url)
            _platform.Open(url);
        else
            await Dialogs.InfoAsync(_window, "Pairnets", "Your nest has no website yet. On the server, give it its own name with: sudo ./install.sh --public-url https://nest.example.com");
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
            if (!await Dialogs.ConfirmAsync(_window, "Pairnets – sign out",
                    $"Sign out of this computer?\n\nSyncing stops. Your files in {_settings.Folder} stay here. Sign in again to continue.", "Sign out", "Cancel"))
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
            SaveSettings();
            _dirty = true;
            if (!removed)
                await Dialogs.InfoAsync(_window, "Pairnets", "Signed out here, but your nest could not be told. " + Relay.RemoveByHandHint(_settings.ServerUrl));
            _window?.Navigate(MainPage.Overview);
        }
        finally
        {
            _signingOut = false;
        }
        ShowSignIn();
    }

    private bool _signingOut;

    private bool _resetting;

    /// <summary>
    /// "Reset this app": removes this computer from the nest (best effort), stops syncing, forgets every setting, the
    /// saved key and the sync notes, and starts again from the sign-in window. The files in the folder stay.
    /// </summary>
    public async void ResetEverything()
    {
        if (_resetting)
            return;
        var where = string.IsNullOrWhiteSpace(_settings.Folder) ? "your sync folder" : _settings.Folder;
        if (!await Dialogs.ConfirmAsync(_window, "Pairnets – reset",
                $"Reset Pairnets on this computer?\n\nThis signs this computer out of your nest, forgets every setting and the saved sign-in, and removes Pairnets' sync notes. Your files in {where} are not deleted. The log files are kept.\n\nYou start again from the sign-in screen.", "Reset", "Cancel"))
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
            var leftFileManager = false;
            try
            {
                leftFileManager = _platform.SetFileManagerEntry(null);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not take Pairnets out of the file manager");
            }
            var result = LocalReset.Run(_settings, _platform.Secrets, _env.SettingsPath, _env.LocalDir);
            var notes = new List<string>(result.Problems);
            if (!leftFileManager)
                notes.Add("Pairnets could not be taken out of the file manager completely.");
            try
            {
                _platform.SetAutoStart(false); // not SetAutoStart(): that would save the settings file again
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add("Start at login could not be turned off: " + FriendlyErrors.Describe(ex));
            }
            if (hadOwnKey && !removed)
                notes.Add("Your nest could not be told. " + Relay.RemoveByHandHint(serverUrl));
            _log.LogInformation("Pairnets was reset on this computer ({Folders} sync folder(s) cleared, {Problems} problem(s))", result.StateFoldersRemoved, result.Problems.Count);

            _settings = new ClientSettings();
            _fileLog.Minimum = LogLevel.Information;
            _updates.SetEnabled(true);
            (_updateDismissed, _updateProgress, _updateError, _serverPromptShownFor, _lowSpaceNoticed) = (false, null, null, null, false);
            _lastNotice.Clear();
            if (_autoStartItem is not null)
                _autoStartItem.IsChecked = SafeIsAutoStart();
            _dirty = true;
            if (notes.Count > 0)
                await Dialogs.InfoAsync(_window, "Pairnets", "Pairnets was reset, with these notes:\n\n• " + string.Join("\n• ", notes));
            _window?.Navigate(MainPage.Overview);
            ShowSignIn();
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
            _log.LogWarning("Could not store this computer's new key: {Error}", ex.Message);
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
            await Dialogs.InfoAsync(_window, "Pairnets", "Pairnets could not rename this computer. " + FriendlyErrors.Describe(ex));
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
        Task.Run(async () => await s.DisposeAsync()).Wait(TimeSpan.FromSeconds(30));
        _dirty = true;
    }

    private void Notify(string key, string title, string text)
    {
        if (!NotifyPolicy.Allows(_settings, key))
            return; // turned off in the notification switches
        lock (_lastNotice)
        {
            var now = DateTime.UtcNow;
            if (_lastNotice.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(10))
                return;
            _lastNotice[key] = now;
        }
        Task.Run(() => _platform.Notify(title, text));
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
        if (_trayMenu is not null)
        {
            // What it is doing in one line: "Checking files · 12,000 of 38,206", "Uploading · 37 of 120 files · 4.90 MB/s · …".
            var line = status.StatusLine;
            if (_tray is not null)
            {
                if (_shownStatus != status.IconStatus)
                {
                    _tray.Icon = IconFor(status.IconStatus);
                    _shownStatus = status.IconStatus;
                }
                _tray.ToolTipText = $"Pairnets: {line} ({status.LastSyncText.ToLowerInvariant()})";
            }
            _statusItem!.Header = line.Length > 60 ? line[..57] + "..." : line;
            _fixItem!.Header = status.FixLabel ?? "No action needed";
            _fixItem.IsEnabled = status.FixLabel is not null;
            _pauseItem!.Header = status.Paused ? "Resume syncing" : "Pause syncing";
        }
        if (_window is { IsVisible: true })
        {
            DrawUpdateCard(_window);
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
            items.Add(new AttentionItem("Syncing is paused until you decide", status.Text, status.FixLabel, FixBlocked));
        foreach (var w in _session.Warnings())
            items.Add(new AttentionItem(w.Path, w.Message ?? w.Code, "Show in folder", () => _platform.Reveal(LocalPath(w.Path)), w.Code == ErrorCodes.CaseCollision ? AttentionKind.NameClash : AttentionKind.Warning));
        foreach (var c in _session.Activity.Items.Where(i => i.Kind == ActivityKind.Conflict && i.Path is not null).Take(20))
        {
            if (File.Exists(LocalPath(c.Path!)))
                items.Add(new AttentionItem("Conflict copy: " + PathRules.FileName(c.Path!), "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => _platform.Reveal(LocalPath(c.Path!)), AttentionKind.Conflict, null, c.Time));
        }
        return items;
    }

    private string LocalPath(string syncPath) => Path.Combine(_settings.Folder ?? string.Empty, PathRules.ToOsRelative(syncPath));

    // ------------------------------------------------------------------ actions

    public void ShowMainWindow()
    {
        if (!_settings.IsComplete)
        {
            ShowSignIn();
            return;
        }
        var opening = _window is not { IsVisible: true };
        if (_window is null)
        {
            _window = new MainWindow(this) { SideFolded = _settings.SideFolded };
            _window.SideFoldedChanged += folded =>
            {
                _settings.SideFolded = folded;
                SaveSettings();
            };
        }
        _window.Show();
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
            ShowSettings();
        else
            _window?.Navigate(page);
    }

    /// <summary>
    /// A pairnets:// link from the nest's website: bring whatever Pairnets is showing to the front.
    /// During first-time setup that is the sign-in window, which is just about to finish on its own.
    /// </summary>
    public void ComeToFront()
    {
        foreach (var window in _lifetime.Windows)
        {
            if (window is SettingsWindow { IsVisible: true })
            {
                window.Activate();
                return;
            }
        }
        ShowMainWindow();
    }

    public IHistorySource? History => _session;

    public INestFiles? Files => _session?.Files;

    public void OpenFile(string path) => _platform.Open(path);

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

    public void OpenAccountSite() => _platform.Open(Relay.DefaultServiceUrl.ToString());

    /// <summary>The Devices page was opened: ask the server for the current list.</summary>
    public void RefreshDevices()
    {
        if (_session is { } session)
            _ = session.RefreshDevicesAsync();
    }

    /// <summary>Saves the icon chosen for a computer on the nest; false when the nest cannot (too old, or unreachable).</summary>
    public Task<bool> SetDeviceIcon(string id, string icon) =>
        _session is { } session ? session.SetDeviceIconAsync(id, icon) : Task.FromResult(false);

    public void RevealFile(string syncPath) => _platform.Reveal(LocalPath(syncPath));

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
        SaveSettings();
    }

    public async void FixBlocked()
    {
        if (_session is null)
        {
            // Signed out here: the Overview's and the tray menu's "Sign in…" open the sign-in window.
            if (StatusSnapshot.WithoutSession(_settings).BlockReason == BlockReason.SignedOutByYou)
                ShowSignIn();
            return;
        }
        var status = _session.Status;
        switch (status.BlockReason)
        {
            case BlockReason.MassDelete:
            case BlockReason.FolderEmpty:
                if (_session.PendingDeletes().Count is > 0 and var pending
                    && await Dialogs.ConfirmAsync(_window, "Pairnets – allow deletions?", _session.DescribePendingDeletes(),
                        OverviewModel.DeleteButtons(pending).Yes, OverviewModel.DeleteButtons(pending).No))
                    _session.ApproveDeletions();
                break;
            case BlockReason.ForeignMarker:
                if (await Dialogs.ConfirmAsync(_window, "Pairnets", $"{_settings.Folder} was synced by Pairnets before, but this computer has no record of it (for example after reinstalling).\n\n" +
                        "Continue syncing it? Files are merged: nothing is deleted, differing files become conflict copies."))
                    _session.AdoptExistingMarker();
                break;
            case BlockReason.ServerChanged:
            case BlockReason.ServerRolledBack:
                if (await Dialogs.ConfirmAsync(_window, "Pairnets", status.Text + "\n\nRe-link? Pairnets will merge this folder with the server: nothing is deleted or overwritten, files that differ become conflict copies."))
                    _session.RelinkToServer();
                break;
            case BlockReason.FolderMissing:
            case BlockReason.MarkerMissing:
            case BlockReason.MarkerMismatch:
                await LocateFolderAsync(status.Text);
                break;
            case BlockReason.SignedOut:
            case BlockReason.SignInRequired:
                ShowSignIn();
                break;
        }
        _dirty = true;
    }

    private async Task LocateFolderAsync(string message)
    {
        await Dialogs.InfoAsync(_window, "Pairnets", message + "\n\nIf the drive is unplugged, plug it in and choose Sync now. If you moved or renamed the folder, choose its new location next.");
        var host = _window ?? new MainWindow(this);
        if (await Dialogs.PickFolderAsync(host, "Where is your Pairnets folder now?") is not { } newFolder || _session is null)
            return;
        var error = _session.CheckMovedFolder(newFolder);
        if (error is not null)
        {
            await Dialogs.InfoAsync(_window, "Pairnets", error);
            return;
        }
        var oldStateDir = _session.StateDirectory;
        StopSession();
        try
        {
            StateLocator.AdoptState(oldStateDir, newFolder, _env.LocalDir);
        }
        catch (IOException ex)
        {
            await Dialogs.InfoAsync(_window, "Pairnets", FriendlyErrors.Describe(ex));
        }
        _settings.Folder = newFolder;
        SaveSettings();
        StartSession(null);
    }

    /// <summary>Settings is a page of the main window (first-time setup has a window of its own).</summary>
    public void ShowSettings()
    {
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSignIn();
            return;
        }
        if (_window is not { IsVisible: true })
            ShowMainWindow();
        var view = new SettingsView(_settings, _platform.Secrets, SafeIsAutoStart(), _updates, _session?.Status.ServerVersionText, _session?.Files);
        view.OpenFilesRequested += () => _window?.Navigate(MainPage.Files);
        view.Saved += v =>
        {
            ApplySettings(v.Result!, v.PlainToken);
            _window?.Navigate(MainPage.Overview);
        };
        view.SwitchesChanged += ApplySwitches;
        view.ReportBugRequested += ReportBug;
        view.ResetRequested += ResetEverything;
        view.ShowDevicesRequested += () => _window?.Navigate(MainPage.Devices);
        view.OpenLinkRequested += url => _platform.Open(url);
        view.Cancelled += () => _window?.Navigate(MainPage.Overview);
        _window!.ShowSettingsPage(view);
    }

    /// <summary>"Sign in again" on the Account page: the sign-in window.</summary>
    public void SignInAgain() => ShowSignIn();

    /// <summary>The app is unsigned on Mac and Linux, so it does not replace itself: open the download page.</summary>
    private void DrawUpdateCard(MainWindow window)
    {
        if (_updates.Available is not { } update || _updateDismissed)
            window.ShowUpdate(null, string.Empty, string.Empty);
        else if (!update.Signed)
            window.ShowUpdate($"Pairnets {update.Version} could not be installed", UpdateProblem.NotSigned.Reason, UpdateProblem.NotSigned.ButtonText);
        else if (_updateError is { } problem)
            window.ShowUpdate($"Pairnets {update.Version} could not be installed", problem.Reason, problem.ButtonText);
        else if (_updateProgress is { } p)
            window.ShowUpdate($"Downloading Pairnets {update.Version}…", "Syncing continues while it downloads.", UpdateTexts.UpdateNow, p, busy: true);
        else if (_package.Method == UpdateMethod.ByHand)
            window.ShowUpdate($"Pairnets {update.Version} is available", UpdateTexts.ByHand(PairnetsInfo.ProductVersion), UpdateTexts.OpenDownloadPage);
        else
            window.ShowUpdate($"Pairnets {update.Version} is available", UpdateTexts.Ready(PairnetsInfo.ProductVersion), UpdateTexts.UpdateNow);
    }

    /// <summary>
    /// The Linux app downloads pairnets-desktop-linux-x64.tar.gz, checks it against the signed SHA256SUMS.txt, swaps its
    /// own file for the new one (<see cref="SelfReplace"/>), starts it and quits. On a Mac the card has already said why
    /// Pairnets can't do that yet, and its button opens the download page. When anything fails, the card says why and
    /// offers to try again; it never sends the person to the browser instead.
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
        if (_package.Method == UpdateMethod.ByHand)
        {
            _platform.Open(update.ReleasePage.ToString());
            return;
        }
        var download = Path.Combine(Path.GetTempPath(), $"Pairnets-{update.Version}-{update.AssetName}");
        var app = _env.AppFile;
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
            if (app is null)
                throw new IOException("Pairnets could not find its own file.");
            _log.LogInformation("Replacing {App} with Pairnets {Version}", app, update.Version);
            try
            {
                await Task.Run(() => SelfReplace.Install(download, _package.AppFile!, app));
            }
            finally
            {
                File.Delete(download);
            }
        }
        catch (Exception ex) when (ex is UpdateVerificationException or HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            _log.LogWarning("Update failed: {Error}", FriendlyErrors.Technical(ex));
            _updateError = UpdateProblem.From(ex, app);
            _updateProgress = null;
            _dirty = true;
            return;
        }
        try
        {
            _platform.StartUpdatedApp(app, SelfReplace.AfterUpdateArgument);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning("Could not start the new Pairnets: {Error}", ex.Message);
            _updateError = new UpdateProblem($"Pairnets {update.Version} is installed, but it couldn't be started. Quit Pairnets and start it again to use the new version.", UpdateNextStep.TryAgain);
            _updateProgress = null;
            _dirty = true;
            return;
        }
        Quit();
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

    private void OnServerInfo(ClientSession session, ServerInfo info)
    {
        if (!ReferenceEquals(session, _session))
            return;
        if (session.Status.ServerSpaceLow && !_lowSpaceNoticed)
        {
            _lowSpaceNoticed = true;
            Notify("disk", "Your nest is running out of space", $"{session.Status.ServerFreeText}. Free up space on your nest or delete old history.");
        }
        if (!session.ServerNeedsUpdate(info) || _serverPromptShownFor == info.ServerVersion)
            return;
        _serverPromptShownFor = info.ServerVersion;
        if (_settings.AutoUpdateServer)
        {
            _ = UpdateServerQuietlyAsync(session, info.ServerVersion!);
            return;
        }
        ShowServerUpdate(info.ServerVersion!, null);
    }

    private async Task UpdateServerQuietlyAsync(ClientSession session, string serverVersion)
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
            Notify("server-updated", "Server updated", $"Your server now runs {result.ServerVersion}.");
        else if (!result.CanUpdateItself)
            Dispatcher.UIThread.Post(() => ShowServerUpdate(serverVersion, result)); // needs the one-time setup
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
                SaveSettings();
        };
        dialog.Show();
    }

    private SettingsWindow? _signInWindow;

    /// <summary>The sign-in window: the only way to set up (or set up again) this computer.</summary>
    private void ShowSignIn()
    {
        // Already open (another tray click while signed out): bring it forward, don't open a second one.
        if (_signInWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new SettingsWindow(_settings, _platform.Secrets, SafeIsAutoStart(), _platform.Open, _session?.Status.NestUrl);
        _signInWindow = window;
        window.Closed += (_, _) =>
        {
            _signInWindow = null;
            if (window.Result is null)
                return;
            ApplySettings(window.Result, window.PlainToken);
            ShowMainWindow();
        };
        window.Show();
        window.Activate();
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
        SaveSettings();
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
                    UpdateFileManagerEntry();
            }
        }
        _dirty = true;
    }

    private void ApplySettings(ClientSettings settings, string? plainToken)
    {
        _settings = settings;
        _fileLog.Minimum = _settings.DebugMode ? LogLevel.Debug : LogLevel.Information;
        SaveSettings();
        SetAutoStart(_settings.StartWithWindows);
        _updates.SetEnabled(_settings.CheckForUpdates);
        StartSession(plainToken);
    }

    private void SaveSettings() => SettingsStore.Save(_env.SettingsPath, _settings);

    public void OpenFolder()
    {
        if (_settings.Folder is { } f && Directory.Exists(f))
            _platform.Open(f);
    }

    public void ViewLog() => _platform.Open(_fileLog.CurrentFile);

    /// <summary>"Report a bug": a report to paste to whoever helps (copied and saved; nothing is sent).</summary>
    public void ReportBug() => ShowBugReport(null);

    private void ShowBugReport(Exception? error)
    {
        var settings = _settings;
        var session = _session;
        var window = new BugReportWindow(() => BugReport.BuildAsync(settings, session, _fileLog.CurrentFile, error, AppName, _env.CrashesDir), _platform.Open,
            afterError: error is not null, saveDirectory: _env.LogsDir);
        window.Show();
        window.Activate();
    }

    /// <summary>An error nothing else caught: log it, keep running, and offer a bug report with it.</summary>
    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _crashes.Record(e.Exception, CrashPlace.Screen);
        e.Handled = true;
        if (_reportingError)
            return; // the report window itself failed; do not loop
        _reportingError = true;
        try
        {
            ShowBugReport(e.Exception);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not open the bug report");
        }
        finally
        {
            _reportingError = false;
        }
    }

    private bool SafeIsAutoStart()
    {
        try
        {
            return _platform.IsAutoStartEnabled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetAutoStart(bool enabled)
    {
        try
        {
            _platform.SetAutoStart(enabled);
            _settings.StartWithWindows = enabled;
            SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not change start-at-login: {Error}", ex.Message);
        }
        if (_autoStartItem is not null)
            _autoStartItem.IsChecked = SafeIsAutoStart();
    }

    public void Quit()
    {
        _panel?.Close();
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _lifetime.Shutdown();
    }

    public void Dispose()
    {
        Dispatcher.UIThread.UnhandledException -= OnUnhandledException;
        _crashes.Dispose();
        _activation?.Dispose();
        _updates.Dispose();
        _refresh.Stop();
        StopSession();
        if (_tray is not null)
            _tray.IsVisible = false;
        _loggers.Dispose();
        _fileLog.Dispose();
    }

    private sealed class AvaloniaDesktop(IClassicDesktopStyleApplicationLifetime lifetime) : IDesktopLifetime
    {
        public IReadOnlyList<Window> Windows => lifetime.Windows;

        public void Shutdown() => lifetime.Shutdown();
    }

    // ------------------------------------------------------------------ for the tests that press every button

    /// <summary>The menu of the tray / menu-bar icon (also when no icon is shown).</summary>
    internal NativeMenu? TrayMenu => _trayMenu;

    /// <summary>A click on the tray icon, through the same handler the icon uses.</summary>
    internal void ClickTrayIcon() => OnTrayClicked(_tray, EventArgs.Empty);

    internal MainWindow? Window => _window;

    /// <summary>"Check now" without the Settings page.</summary>
    internal Task CheckForUpdateForTestsAsync() => _updates.CheckNowAsync(CancellationToken.None);

    internal TrayPanel? Panel => _panel;

    internal ClientSession? Session => _session;

    internal ClientSettings Settings => _settings;

    internal string LogFile => _fileLog.CurrentFile;

    /// <summary>Redraws the windows and the menu now instead of at the next tick.</summary>
    internal void RefreshNow()
    {
        _dirty = true;
        RefreshIfDirty();
    }
}
