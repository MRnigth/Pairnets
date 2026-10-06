using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Tether.Core;
using Tether.Core.Client;
using Tether.Core.Logging;
using Tether.Core.Paths;
using Tether.Core.Settings;
using Tether.Core.Sync;
using Tether.Desktop.Platform;
using Tether.Desktop.Views;

namespace Tether.Desktop;

/// <summary>
/// Menu-bar (macOS) / system-tray (Linux) icon, main window and notifications around a
/// <see cref="ClientSession"/>. Mirrors the Windows TrayController; all sync logic is in Tether.Core.
/// </summary>
public sealed class DesktopController : ITrayActions, IDisposable
{
    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IPlatformServices _platform;
    private readonly RollingFileLoggerProvider _fileLog;
    private bool _reportingError;
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
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _fixItem;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _autoStartItem;
    private volatile bool _dirty = true;
    private RunnerStatus? _shownStatus;
    private readonly UpdateService _updates;
    private bool _updateDismissed;
    private string? _serverPromptShownFor;
    private bool _lowSpaceNoticed;

    public DesktopController(Application app, IClassicDesktopStyleApplicationLifetime lifetime, IPlatformServices platform)
    {
        _app = app;
        _lifetime = lifetime;
        _platform = platform;
        _fileLog = new RollingFileLoggerProvider(TetherPaths.LogsDir, retentionDays: 14);
        _loggers = LoggerFactory.Create(b => b.AddProvider(_fileLog).SetMinimumLevel(LogLevel.Debug));
        _log = _loggers.CreateLogger("Tether.Desktop");
        _settings = SettingsStore.Load(SettingsStore.DefaultPath);
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        _fileLog.Minimum = _settings.DebugMode ? LogLevel.Debug : LogLevel.Information; // Debug mode: more detail in the log
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshIfDirty());
        _updates = new UpdateService(new UpdateChecker(new HttpClient()), TetherInfo.ProductVersion, UpdateChecker.AssetForThisPlatform());
        _updates.UpdateAvailable += u =>
        {
            _updateDismissed = false;
            _dirty = true;
            Notify("update:" + u.Version, "Tether update available", $"Version {u.Version} is ready. Open Tether to download it.");
        };
    }

    public void Start(string[] args)
    {
        _log.LogInformation("Tether {Version} starting on {Platform}", typeof(DesktopController).Assembly.GetName().Version, _platform.Name);
        CreateTray();
        _refresh.Start();
        _updates.SetEnabled(_settings.CheckForUpdates);
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSettings(firstRun: true);
            return;
        }
        StartSession(null);
        if (!args.Contains("--autostart"))
            ShowMainWindow();
    }

    // ------------------------------------------------------------------ tray / menu bar

    private void CreateTray()
    {
        var menu = new NativeMenu();
        var quick = new NativeMenuItem("Quick status…");
        quick.Click += (_, _) => TogglePanel();
        var open = new NativeMenuItem("Open Tether");
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
        var quit = new NativeMenuItem("Quit Tether");
        quit.Click += (_, _) => Quit();
        foreach (var item in new NativeMenuItemBase[] { quick, open, _statusItem, new NativeMenuItemSeparator(), sync, folder, _fixItem,
                     new NativeMenuItemSeparator(), settings, log, report, _pauseItem, _autoStartItem, new NativeMenuItemSeparator(), quit })
            menu.Items.Add(item);

        _tray = new TrayIcon
        {
            Icon = IconFor(RunnerStatus.Offline),
            ToolTipText = "Tether",
            Menu = menu,
            IsVisible = true,
        };
        // A click opens the quick-look panel where the desktop reports clicks (most Linux trays;
        // the macOS menu bar always shows the menu, which starts with "Quick status…").
        _tray.Clicked += (_, _) => TogglePanel();
        TrayIcon.SetIcons(_app, [_tray]);
    }

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
                        ctx.DrawGeometry(null, new Pen(Brushes.White, 3.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), glyph);
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
            icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Tether/Assets/tether-icon-256.png")));
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
            Notify("token", "Tether needs the token again", "The saved token cannot be read. Open Settings and enter it.");
            return;
        }

        var session = ClientSession.Start(_settings, plainToken, _platform.CreateTrash(_loggers.CreateLogger("Tether.Trash")), _loggers);
        session.StatusChanged += _ => _dirty = true;
        session.Activity.Added += _ => _dirty = true;
        session.ConflictCreated += c => Notify("conflict:" + c.Path, "Conflict: both computers changed a file",
            $"{c.Path}: your version was kept as \"{PathRules.FileName(c.ConflictCopyPath)}\".");
        session.PathWarningRaised += w => Notify("warning:" + w.Path, w.Code == ErrorCodes.CaseCollision ? "Name collision" : "File name not allowed", $"{w.Path}: {w.Message}");
        session.PassCompleted += r =>
        {
            if (r.Outcome == PassOutcome.Blocked)
                Notify("blocked:" + r.BlockReason, r.BlockReason is BlockReason.MassDelete or BlockReason.FolderEmpty ? "Deletions blocked" : "Tether paused syncing", r.Message ?? r.BlockReason.ToString());
            else if (r.Outcome == PassOutcome.AuthFailed)
                Notify("auth", "The server rejected the token", "Open Settings and enter the token printed by install.sh.");
        };
        session.ServerInfoChanged += info => Dispatcher.UIThread.Post(() => OnServerInfo(session, info));
        session.CatchUpCompleted += n => Notify("catchup", "Tether is up to date", $"Synced {n} change(s) made while this computer was away.");
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

    private void Notify(string key, string title, string text)
    {
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
        // Redraw every 2 seconds while a window shows, so "2 min ago" and the map's moving dots stay current.
        if (++_ticks % 8 == 0 && (_window is { IsVisible: true } || _panel is { IsVisible: true }))
            _dirty = true;
        if (!_dirty)
            return;
        _dirty = false;
        var status = _session?.Status ?? StatusSnapshot.Initial with { Text = "Not set up yet: open Settings" };
        if (_tray is not null)
        {
            if (_shownStatus != status.Status)
            {
                _tray.Icon = IconFor(status.Status);
                _shownStatus = status.Status;
            }
            var line = status.IsTransferring
                ? $"{PathRules.FileName(status.CurrentPath!)}{(status.Percent is { } p ? $" {p}%" : string.Empty)}"
                : status.Text;
            _tray.ToolTipText = $"Tether: {line} ({status.LastSyncText.ToLowerInvariant()})";
            _statusItem!.Header = line.Length > 60 ? line[..57] + "..." : line;
            _fixItem!.Header = status.FixLabel ?? "No action needed";
            _fixItem.IsEnabled = status.FixLabel is not null;
            _pauseItem!.Header = status.Paused ? "Resume syncing" : "Pause syncing";
        }
        if (_window is { IsVisible: true })
        {
            if (_updates.Available is { } update && !_updateDismissed)
                _window.ShowUpdate($"Tether {update.Version} is available",
                    $"You have {TetherInfo.ProductVersion}. Download it and replace the app (your settings are kept).", "Download");
            else
                _window.ShowUpdate(null, string.Empty, string.Empty);
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
        if (status.FixLabel is not null)
            items.Add(new AttentionItem("Syncing is paused until you decide", status.Text, status.FixLabel, FixBlocked));
        foreach (var w in _session.Warnings())
            items.Add(new AttentionItem(w.Path, w.Message ?? w.Code, "Show in folder", () => _platform.Reveal(LocalPath(w.Path))));
        foreach (var c in _session.Activity.Items.Where(i => i.Kind == ActivityKind.Conflict && i.Path is not null).Take(20))
        {
            if (File.Exists(LocalPath(c.Path!)))
                items.Add(new AttentionItem("Conflict copy: " + PathRules.FileName(c.Path!), "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => _platform.Reveal(LocalPath(c.Path!))));
        }
        return items;
    }

    private string LocalPath(string syncPath) => Path.Combine(_settings.Folder ?? string.Empty, PathRules.ToOsRelative(syncPath));

    // ------------------------------------------------------------------ actions

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

    public IHistorySource? History => _session;

    /// <summary>The Devices page was opened: ask the server for the current list.</summary>
    public void RefreshDevices()
    {
        if (_session is { } session)
            _ = session.RefreshDevicesAsync();
    }

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
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
    }

    public async void FixBlocked()
    {
        if (_session is null)
            return;
        var status = _session.Status;
        switch (status.BlockReason)
        {
            case BlockReason.MassDelete:
            case BlockReason.FolderEmpty:
                if (_session.PendingDeletes().Count > 0 && await Dialogs.ConfirmAsync(_window, "Tether – allow deletions?", _session.DescribePendingDeletes()))
                    _session.ApproveDeletions();
                break;
            case BlockReason.ForeignMarker:
                if (await Dialogs.ConfirmAsync(_window, "Tether", $"{_settings.Folder} was synced by Tether before, but this computer has no record of it (for example after reinstalling).\n\n" +
                        "Continue syncing it? Files are merged: nothing is deleted, differing files become conflict copies."))
                    _session.AdoptExistingMarker();
                break;
            case BlockReason.ServerChanged:
            case BlockReason.ServerRolledBack:
                if (await Dialogs.ConfirmAsync(_window, "Tether", status.Text + "\n\nRe-link? Tether will merge this folder with the server: nothing is deleted or overwritten, files that differ become conflict copies."))
                    _session.RelinkToServer();
                break;
            case BlockReason.FolderMissing:
            case BlockReason.MarkerMissing:
            case BlockReason.MarkerMismatch:
                await LocateFolderAsync(status.Text);
                break;
        }
        _dirty = true;
    }

    private async Task LocateFolderAsync(string message)
    {
        await Dialogs.InfoAsync(_window, "Tether", message + "\n\nIf the drive is unplugged, plug it in and choose Sync now. If you moved or renamed the folder, choose its new location next.");
        var host = _window ?? new MainWindow(this);
        var picked = await host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where is your Tether folder now?" });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } newFolder || _session is null)
            return;
        var error = _session.CheckMovedFolder(newFolder);
        if (error is not null)
        {
            await Dialogs.InfoAsync(_window, "Tether", error);
            return;
        }
        var oldStateDir = _session.StateDirectory;
        StopSession();
        try
        {
            StateLocator.AdoptState(oldStateDir, newFolder);
        }
        catch (IOException ex)
        {
            await Dialogs.InfoAsync(_window, "Tether", ex.Message);
        }
        _settings.Folder = newFolder;
        SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        StartSession(null);
    }

    /// <summary>Settings is a page of the main window (first-time setup has a window of its own).</summary>
    public void ShowSettings()
    {
        if (!_settings.IsComplete || !_settings.FirstRunCompleted)
        {
            ShowSettings(firstRun: true);
            return;
        }
        if (_window is not { IsVisible: true })
            ShowMainWindow();
        var view = new SettingsView(_settings, _platform.Secrets, firstRun: false, SafeIsAutoStart(), _updates, _session?.Status.ServerVersionText);
        view.Saved += v =>
        {
            ApplySettings(v.Result!, v.PlainToken);
            _window?.Navigate(MainPage.Overview);
        };
        view.Cancelled += () => _window?.Navigate(MainPage.Overview);
        _window!.ShowSettingsPage(view);
    }

    /// <summary>The app is unsigned on Mac and Linux, so it does not replace itself: open the download page.</summary>
    public void UpdateNow()
    {
        if (_updates.Available is { } update)
            _platform.Open(update.ReleasePage.ToString());
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
            Notify("disk", "The server is running out of space", $"{session.Status.ServerFreeText}. Free up space on the server or delete old history.");
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
                SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        };
        dialog.Show();
    }

    private void ShowSettings(bool firstRun)
    {
        var window = new SettingsWindow(_settings, _platform.Secrets, firstRun, SafeIsAutoStart(), _updates, _session?.Status.ServerVersionText);
        window.Closed += (_, _) =>
        {
            if (window.Result is null)
                return;
            ApplySettings(window.Result, window.PlainToken);
            ShowMainWindow();
            if (firstRun)
                _window?.ExpectFirstSync(); // confetti when the first sync is done
        };
        window.Show();
        window.Activate();
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
        var app = OperatingSystem.IsMacOS() ? "Mac app" : "Linux app";
        var window = new BugReportWindow(() => BugReport.BuildAsync(settings, session, _fileLog.CurrentFile, error, app), _platform.Open, afterError: error is not null);
        window.Show();
        window.Activate();
    }

    /// <summary>An error nothing else caught: log it, keep running, and offer a bug report with it.</summary>
    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.LogError(e.Exception, "Unhandled UI exception");
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
            SettingsStore.Save(SettingsStore.DefaultPath, _settings);
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
        _updates.Dispose();
        _refresh.Stop();
        StopSession();
        if (_tray is not null)
            _tray.IsVisible = false;
        _loggers.Dispose();
        _fileLog.Dispose();
    }
}
