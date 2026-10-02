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
public sealed class DesktopController : IMainActions, IDisposable
{
    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IPlatformServices _platform;
    private readonly RollingFileLoggerProvider _fileLog;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly Dictionary<RunnerStatus, WindowIcon> _icons = [];
    private readonly Dictionary<string, DateTime> _lastNotice = [];
    private readonly DispatcherTimer _refresh;
    private ClientSettings _settings;
    private ClientSession? _session;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _fixItem;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _autoStartItem;
    private volatile bool _dirty = true;
    private RunnerStatus? _shownStatus;

    public DesktopController(Application app, IClassicDesktopStyleApplicationLifetime lifetime, IPlatformServices platform)
    {
        _app = app;
        _lifetime = lifetime;
        _platform = platform;
        _fileLog = new RollingFileLoggerProvider(TetherPaths.LogsDir, retentionDays: 14);
        _loggers = LoggerFactory.Create(b => b.AddProvider(_fileLog).SetMinimumLevel(LogLevel.Information));
        _log = _loggers.CreateLogger("Tether.Desktop");
        _settings = SettingsStore.Load(SettingsStore.DefaultPath);
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshIfDirty());
    }

    public void Start(string[] args)
    {
        _log.LogInformation("Tether {Version} starting on {Platform}", typeof(DesktopController).Assembly.GetName().Version, _platform.Name);
        CreateTray();
        _refresh.Start();
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
        _pauseItem = new NativeMenuItem("Pause syncing");
        _pauseItem.Click += (_, _) => TogglePause();
        _autoStartItem = new NativeMenuItem("Start at login") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = SafeIsAutoStart() };
        _autoStartItem.Click += (_, _) => SetAutoStart(!SafeIsAutoStart());
        var quit = new NativeMenuItem("Quit Tether");
        quit.Click += (_, _) => Quit();
        foreach (var item in new NativeMenuItemBase[] { open, _statusItem, new NativeMenuItemSeparator(), sync, folder, _fixItem,
                     new NativeMenuItemSeparator(), settings, log, _pauseItem, _autoStartItem, new NativeMenuItemSeparator(), quit })
            menu.Items.Add(item);

        _tray = new TrayIcon
        {
            Icon = IconFor(RunnerStatus.Offline),
            ToolTipText = "Tether",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(_app, [_tray]);
    }

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

    private void RefreshIfDirty()
    {
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
        _window ??= new MainWindow(this);
        _window.Show();
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

    public void ShowSettings() => ShowSettings(firstRun: false);

    private void ShowSettings(bool firstRun)
    {
        var window = new SettingsWindow(_settings, _platform.Secrets, firstRun, SafeIsAutoStart());
        window.Closed += (_, _) =>
        {
            if (window.Result is null)
                return;
            _settings = window.Result;
            SettingsStore.Save(SettingsStore.DefaultPath, _settings);
            SetAutoStart(_settings.StartWithWindows);
            StartSession(window.PlainToken);
            ShowMainWindow();
        };
        window.Show();
        window.Activate();
    }

    public void OpenFolder()
    {
        if (_settings.Folder is { } f && Directory.Exists(f))
            _platform.Open(f);
    }

    public void ViewLog() => _platform.Open(_fileLog.CurrentFile);

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

    private void Quit()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        _lifetime.Shutdown();
    }

    public void Dispose()
    {
        _refresh.Stop();
        StopSession();
        if (_tray is not null)
            _tray.IsVisible = false;
        _loggers.Dispose();
        _fileLog.Dispose();
    }
}
