using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Tether.Core;
using Tether.Core.Client;
using Tether.Core.Settings;
using Tether.Core.Sync;
using Tether.Desktop.Platform;
using Tether.Desktop.Views;
using Tether.Tests.Infrastructure;

[assembly: AvaloniaTestApplication(typeof(Tether.Tests.Ui.HeadlessApp))]

namespace Tether.Tests.Ui;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Tether.Desktop.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Sample files and versions for the History page (screenshots and tests).</summary>
public sealed class SampleHistory : IHistorySource
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public List<(string Path, string Id)> Restored { get; } = [];

    public Task<IReadOnlyList<ServerFile>> GetServerFilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerFile>>(
    [
        new("Projects/2026/budget-draft.xlsx", true, _now.AddHours(-2), 0),
        new("Photos/summer/IMG_2041.jpg", true, _now.AddDays(-1).AddHours(-3), 0),
        new("Notes/old-ideas.md", true, _now.AddDays(-5), 0),
        new("Archive/2024/taxes.pdf", true, _now.AddDays(-12), 0),
        new("Archive/2023/ancient.txt", true, _now.AddDays(-50), 0),
        new("Projects/report.docx", false, _now.AddMinutes(-20), 482_304, new string('a', 64)),
        new("Notes/meeting-notes.md", false, _now.AddMinutes(-47), 6_210),
        new("Projects/2026/plan.xlsx", false, _now.AddHours(-3), 91_022),
    ]);

    public Task<IReadOnlyList<HistoryVersion>> GetVersionsAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<HistoryVersion>>(
    [
        new("20261006T100000000Z-c0ffee00", _now.AddHours(-2), 48_220, "c0ffee00"),
        new("20261004T100000000Z-be5eda7a", _now.AddDays(-2).AddHours(-4), 47_100, "be5eda7a"),
        new("20260929T100000000Z-0ddba11a", _now.AddDays(-7), 39_870, "0ddba11a"),
    ]);

    public Task<string?> RestoreVersionAsync(string path, HistoryVersion version, CancellationToken ct)
    {
        Restored.Add((path, version.Id));
        return Task.FromResult<string?>(null);
    }
}

/// <summary>Records what the windows asked for; the History page reads <see cref="SampleHistory"/>.</summary>
public sealed class SampleActions : IMainActions
{
    public List<string> Calls { get; } = [];

    public SampleHistory Samples { get; } = new();

    public IHistorySource? History => Samples;

    public void FixBlocked() => Calls.Add("fix");
    public void SyncNow() => Calls.Add("sync");
    public void TogglePause() => Calls.Add("pause");
    public void OpenFolder() => Calls.Add("folder");
    public void ShowSettings() => Calls.Add("settings");
    public void ViewLog() => Calls.Add("log");
    public void UpdateNow() => Calls.Add("update");
    public void DismissUpdate() => Calls.Add("dismiss");
    public void DownloadNow() => Calls.Add("download");
    public void UpdateServer() => Calls.Add("server");
    public void ReportBug() => Calls.Add("bug");
    public void RevealFile(string syncPath) => Calls.Add("reveal:" + syncPath);
    public void RefreshDevices() => Calls.Add("devices");
}

/// <summary>Loads the macOS/Linux windows headlessly: XAML parses, controls bind, states render.</summary>
public class DesktopUiTests
{
    [AvaloniaFact]
    public void DevicesTabListsTheServersComputers()
    {
        var window = new MainWindow(null) { ThisDevice = "MacBook" };
        window.Show();
        window.ShowDevicesTab();
        Assert.False(window.DevicesShown); // nothing from the server yet: the empty text shows
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Devices =
            [
                new Tether.Core.DeviceInfo("DESKTOP", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, true, "1.0.38", "Windows"),
                new Tether.Core.DeviceInfo("MacBook", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, true, "1.0.38", "macOS"),
            ],
        }, null);
        Assert.True(window.DevicesShown);
        Assert.Equal(2, window.DeviceRows);
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void MainWindowShowsStatusActivityAndFixAction()
    {
        var window = new MainWindow();
        window.Show();
        Assert.Equal(0, window.ActivityCount);

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, CurrentPath = "a/big.bin", Operation = "upload", BytesDone = 5, BytesTotal = 10, FilesTotal = 3 }, "/home/me/Work");
        Assert.Equal("Syncing…", window.HeadlineText);
        Assert.True(window.TransferVisible);
        Assert.False(window.FixVisible);

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 12 }, null);
        Assert.Equal("Needs your decision", window.HeadlineText);
        Assert.True(window.FixVisible);
        Assert.False(window.TransferVisible);

        var feed = new ActivityFeed();
        feed.Add(ActivityKind.Uploaded, "a.txt", "Uploaded: a.txt");
        feed.Add(ActivityKind.Conflict, "b (conflict MAC 2026-10-02 101010).txt", "Conflict on b.txt");
        window.ShowActivity(feed.Items);
        Assert.Equal(2, window.ActivityCount);
        window.ShowAttention([new AttentionItem("x", "y", "Show in folder", () => { })]);
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void MotionFollowsTheState()
    {
        var window = new MainWindow();
        window.Show();
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, CurrentPath = "a.bin", Operation = "upload", FilesTotal = 3,
            Active = [new ActiveTransfer("a.bin", "upload", 10, 100), new ActiveTransfer("b.bin", "upload", 50, 100)],
        }, null);
        Assert.True(window.GlyphSpins);
        Assert.Equal("rise", window.ArrowMotion);
        Assert.Equal(2, window.ActiveRows);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, CurrentPath = "c.bin", Operation = "download", FilesTotal = 3,
            Active = [new ActiveTransfer("c.bin", "download", 10, 100)],
        }, null);
        Assert.Equal("fall", window.ArrowMotion);
        Assert.Equal(1, window.ActiveRows);

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, WaitingFor = new PeerWait("DESKTOP", 340, 12) }, null);
        Assert.False(window.GlyphSpins);
        Assert.True(window.BadgePulses);
        window.AllowClose = true;
        window.Close();

        var unknownVersion = new ServerUpdateWindow(null, string.Empty, "1.0.58");
        unknownVersion.Show();
        Assert.Equal("Your server should be updated", unknownVersion.HeadingText);
        unknownVersion.Close();

        var upToDate = new ServerUpdateWindow(null, "1.0.58", "1.0.58");
        upToDate.Show();
        Assert.Equal("Your server is up to date", upToDate.HeadingText);
        Assert.Equal("S.Green", upToDate.BadgeKey); // nothing is wrong: no orange warning badge
        Assert.False(upToDate.Bobbing);
        Assert.False(upToDate.DetailsShown); // only in Debug mode
        upToDate.ShowResult(new ServerUpdateResult(true, "Already up to date (1.0.58).", "1.0.58", CanUpdateItself: true, AlreadyUpToDate: true));
        Assert.Equal("Your server is up to date", upToDate.HeadingText);
        Assert.Equal("S.Green", upToDate.BadgeKey);
        upToDate.Close();

        var debug = new ServerUpdateWindow(null, "1.0.52", "1.0.58", debug: () => true);
        debug.Show();
        Assert.True(debug.DetailsShown);
        Assert.Equal("S.Orange", debug.BadgeKey);
        debug.AppendDetail("[  0s] Asking the server to update");
        debug.ShowResult(new ServerUpdateResult(false, "The updater did not start.", "1.0.52", CanUpdateItself: true,
            Details: "Path unit enabled:   NO"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains("Asking the server to update", debug.DetailsValue);
        Assert.Contains("--- What the server reports ---", debug.DetailsValue);
        Assert.Contains("Path unit enabled:   NO", debug.DetailsValue);
        debug.Close();

        // No session (or settings were just saved): the button explains instead of throwing.
        var noSession = new ServerUpdateWindow(() => null, "1.0.52", "1.0.58");
        noSession.Show();
        Assert.True(noSession.RunUpdateAsync().IsCompleted);
        Assert.Equal("Not updated yet", noSession.HeadingText);
        Assert.Contains("Not connected to the server", noSession.ExplanationText);
        noSession.Close();

        using var reports = new TempDir("bug-reports");
        var bug = new BugReportWindow(() => Task.FromResult("Tether bug report\nline two"), _ => { }, afterError: true, saveDirectory: reports.Path);
        Assert.True(bug.BuildAsync().IsCompleted);
        Assert.Equal("Tether bug report\nline two", bug.ReportValue);
        Assert.NotNull(bug.SavedPath);
        Assert.Equal("Tether bug report\nline two", File.ReadAllText(bug.SavedPath!));

        var dialog = new ServerUpdateWindow(null, "1.0.52", "1.0.58");
        dialog.Show();
        Assert.Equal("Your server should be updated", dialog.HeadingText);
        Assert.True(dialog.Bobbing);
        dialog.ShowBusy("Installing");
        Assert.True(dialog.Spinning);
        Assert.False(dialog.Bobbing);
        dialog.ShowResult(new ServerUpdateResult(false, "Old server", "1.0.52", CanUpdateItself: false));
        Assert.Equal("One-time setup", dialog.HeadingText);
        Assert.True(dialog.CommandShown);
        Assert.DoesNotContain("didn't", dialog.HeadingText);
        dialog.ShowResult(new ServerUpdateResult(false, "The download did not match its checksum.", "1.0.52", CanUpdateItself: true));
        Assert.Equal("Not updated yet", dialog.HeadingText);
        Assert.True(dialog.RetryShown);
        dialog.ShowResult(new ServerUpdateResult(true, "ok", "1.0.58", CanUpdateItself: true));
        Assert.Equal("Server updated to 1.0.58", dialog.HeadingText);
        Assert.False(dialog.Spinning);
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task HistoryPageListsDeletedFilesAndRestoresAVersion()
    {
        var actions = new SampleActions();
        var window = new MainWindow(actions);
        window.Show();
        window.Navigate(MainPage.History);
        await window.LoadHistoryAsync();
        Assert.Equal(4, window.HistoryRows); // the fifth deletion is older than 30 days
        window.ShowAllFiles();
        Assert.Equal(3, window.HistoryRows);
        window.SearchHistory("REPORT");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // TextChanged arrives through the dispatcher
        Assert.Equal(1, window.HistoryRows);
        window.SelectHistoryRow(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, window.VersionRows);
        await window.RestoreFirstVersionAsync();
        Assert.Equal(("Projects/report.docx", "20261006T100000000Z-c0ffee00"), Assert.Single(actions.Samples.Restored));
        Assert.StartsWith("Restored.", window.RestoreText);

        // "Show versions…" on an activity row opens History on that file.
        window.ShowVersionsFor("Notes/old-ideas.md");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(MainPage.History, window.Page);
        Assert.Equal(1, window.HistoryRows);
        Assert.Equal(3, window.VersionRows);
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void SidebarPagesSettingsAndTheMap()
    {
        var actions = new SampleActions();
        var window = new MainWindow(actions);
        window.Show();
        window.ClickNav(MainPage.Settings);
        Assert.Equal(["settings"], actions.Calls); // the controller hands over a fresh form
        window.ShowSettingsPage(new SettingsView(new ClientSettings { ServerUrl = "http://example.invalid:5075/", Folder = "/tmp/x", DeviceName = "mac" }, null, firstRun: false, autoStart: false));
        Assert.Equal(MainPage.Settings, window.Page);
        Assert.True(window.SettingsShown);
        window.ClickNav(MainPage.Activity);
        Assert.Equal(MainPage.Activity, window.Page);
        Assert.False(window.SettingsShown); // next time it starts again from the saved settings

        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(DateTimeOffset.Now.AddDays(-1), ActivityKind.Uploaded, "a.txt", "Uploaded"));
        feed.Add(ActivityKind.Downloaded, "b.txt", "Downloaded");
        window.ShowActivity(feed.Items);
        Assert.Equal(4, window.ActivityRowCount); // two day headings and two rows

        var server = new ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, LastSyncAt = DateTimeOffset.Now, Server = server, CurrentPath = "a", Operation = "upload",
            Active = [new ActiveTransfer("a", "upload", 1, 2)],
            Devices = [new DeviceInfo("mac", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true), new DeviceInfo("DESKTOP", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true)],
        }, "/tmp/x", "mac");
        Assert.Equal("DESKTOP: Online", window.MapOtherText);
        Assert.True(window.MapHereFlowing);
        window.ShowAttention([new AttentionItem("x", "y", "Show in folder", () => { })]);
        Assert.True(window.CalloutVisible);
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void TrayPanelShowsStatusRecentChangesAndAttention()
    {
        var panel = new TrayPanel(null);
        panel.Show();
        panel.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = DateTimeOffset.Now }, "mac");
        Assert.Equal("Up to date", panel.HeadlineText);
        var feed = new ActivityFeed();
        for (var i = 0; i < 6; i++)
            feed.Add(ActivityKind.Uploaded, $"f{i}.txt", "Uploaded");
        panel.ShowActivity(feed.Items);
        Assert.Equal(4, panel.RecentRows);
        Assert.False(panel.AttentionShown);
        panel.ShowAttention(3);
        Assert.True(panel.AttentionShown);
        panel.Close();
    }

    [AvaloniaFact]
    public void SettingsWindowLoadsInFirstRunAndSettingsModes()
    {
        var first = new SettingsWindow(new ClientSettings(), null, firstRun: true, autoStart: false);
        first.Show();
        Assert.Contains("first-time", first.Title);
        first.Close();
        var later = new SettingsWindow(new ClientSettings { ServerUrl = "http://example.invalid:5075/", Folder = "/tmp/x", DeviceName = "mac" }, null, firstRun: false, autoStart: true);
        later.Show();
        Assert.Contains("settings", later.Title);
        later.Close();
    }

    /// <summary>
    /// Renders the windows (light and dark) to PNG files for documentation when TETHER_SCREENSHOT_DIR
    /// is set (otherwise it just checks that frames can be rendered).
    /// </summary>
    [AvaloniaFact]
    public void RenderScreenshots()
    {
        var outDir = Environment.GetEnvironmentVariable("TETHER_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            RenderAll(outDir, suffix);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static void RenderAll(string? outDir, string suffix)
    {
        var now = DateTimeOffset.Now;
        var actions = new SampleActions();
        var window = new MainWindow(actions) { Width = 980, Height = 700 };
        window.Show();
        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(now.AddDays(-1).AddHours(-2), ActivityKind.Uploaded, "Projects/2026/budget.xlsx", "Uploaded"));
        feed.Add(new ActivityItem(now.AddDays(-1), ActivityKind.Downloaded, "Notes/ideas.md", "Downloaded"));
        feed.Add(new ActivityItem(now.AddHours(-3), ActivityKind.Downloaded, "Projects/2026/plan.xlsx", "Downloaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-47), ActivityKind.Uploaded, "Notes/meeting-notes.md", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-12), ActivityKind.DeletedOnServer, "Archive/old-draft.txt", "Deleted"));
        feed.Add(new ActivityItem(now.AddMinutes(-5), ActivityKind.Conflict, "Projects/report (conflict LAPTOP 2026-10-02 141509).docx", "Conflict"));
        feed.Add(new ActivityItem(now.AddMinutes(-2), ActivityKind.Downloaded, "Photos/summer/beach.jpg", "Downloaded"));
        feed.Add(new ActivityItem(now.AddSeconds(-20), ActivityKind.Uploaded, "Photos/summer/holiday.jpg", "Uploaded"));
        window.ShowActivity(feed.Items);
        var attention = new List<AttentionItem>
        {
            new("Conflict copy: report (conflict LAPTOP 2026-10-02 141509).docx",
                "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { }),
            new("Docs/Report.TXT", "Another file with the same name in different letter case exists on the server.", "Show in folder", () => { }),
        };
        window.ShowAttention(attention);

        var server = new Tether.Core.ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
        var utc = DateTimeOffset.UtcNow;
        IReadOnlyList<Tether.Core.DeviceInfo> devices =
        [
            new("MacBook", utc.AddDays(-30), utc, true, "1.0.58", "macOS"),
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
        ];
        window.ShowUpdate("Tether 1.0.58 is available", "You have 1.0.52. Your settings are kept.", "Download");
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1),
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 67_108_864, BytesTotal = 104_857_600,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20, BytesPerSecond = 4.9 * (1 << 20),
            LimitText = "Limited to 5 MB/s", Server = server, Devices = devices,
            Active =
            [
                new ActiveTransfer("Photos/summer/holiday-0412.jpg", "upload", 78, 100),
                new ActiveTransfer("Photos/summer/holiday-0413.jpg", "upload", 41, 100),
                new ActiveTransfer("Videos/presentation-final.mp4", "upload", 12, 100),
                new ActiveTransfer("Notes/meeting-2026-10-02.md", "upload", 95, 100),
            ],
        }, "/Users/me/Work", "MacBook");
        Save(window, outDir, $"main-window-syncing-{suffix}.png");

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, LastSyncAt = now.AddMinutes(-3),
            Text = "This sync would delete 37 files on the server (out of 120). Nothing was deleted.", Server = server, Devices = devices,
        }, "/Users/me/Work", "MacBook");
        Save(window, outDir, $"main-window-blocked-{suffix}.png");
        window.ShowAttentionTab(true);
        Save(window, outDir, $"main-window-attention-{suffix}.png");

        window.ShowUpdate(null, string.Empty, string.Empty);
        window.ShowAttention([]);
        window.ShowAttentionTab(false);
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now, Server = server, Devices = devices }, "/Users/me/Work", "MacBook");
        Save(window, outDir, $"main-window-idle-{suffix}.png");

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-4), Server = server, Devices = devices,
            WaitingFor = new Tether.Core.Sync.PeerWait("DESKTOP", 340, 212),
        }, "/Users/me/Work", "MacBook");
        Save(window, outDir, $"main-window-waiting-{suffix}.png");

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-1), Server = server,
            Devices = [new("MacBook", utc.AddDays(-30), utc, true), new("DESKTOP", utc.AddHours(-3).AddDays(-30), utc.AddHours(-3), false, "1.0.58", "Windows")],
        }, "/Users/me/Work", "MacBook");
        window.Navigate(MainPage.Activity);
        Save(window, outDir, $"main-window-activity-{suffix}.png");

        window.Navigate(MainPage.Devices);
        Save(window, outDir, $"main-window-devices-{suffix}.png");

        window.Navigate(MainPage.History);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.SelectHistoryRow(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Save(window, outDir, $"main-window-history-{suffix}.png");

        window.ShowSettingsPage(new SettingsView(new ClientSettings { ServerUrl = "http://100.x.y.z:5075/", Folder = "/Users/me/Work", DeviceName = "MacBook" },
            null, firstRun: false, autoStart: true, serverVersionText: "Server 1.0.58"));
        Save(window, outDir, $"main-window-settings-{suffix}.png");
        window.AllowClose = true;
        window.Close();

        var panel = new TrayPanel(null);
        panel.Show();
        panel.ShowActivity(feed.Items);
        panel.ShowAttention(2);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1), Server = server, Devices = devices,
            CurrentPath = "Photos/summer/holiday-0412.jpg", Operation = "download", FilesDone = 12, FilesTotal = 30,
            PassBytesDone = 96L << 20, PassBytesTotal = 240L << 20, BytesPerSecond = 8.2 * (1 << 20),
            Active = [new ActiveTransfer("Photos/summer/holiday-0412.jpg", "download", 50, 100)],
            HeardFrom = new Dictionary<string, DateTimeOffset> { ["DESKTOP"] = utc },
        }, "MacBook");
        Save(panel, outDir, $"tray-panel-syncing-{suffix}.png");
        panel.ShowAttention(0);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now, Server = server,
            Devices = [new("MacBook", utc.AddDays(-30), utc, true), new("DESKTOP", utc.AddHours(-3).AddDays(-30), utc.AddHours(-3), false, "1.0.58", "Windows")],
        }, "MacBook");
        Save(panel, outDir, $"tray-panel-idle-{suffix}.png");
        panel.Close();

        var settings = new SettingsWindow(new ClientSettings { DeviceName = "MacBook" }, null, firstRun: true, autoStart: true);
        settings.Show();
        settings.ShowTestResult(true, "Connected. Server and token are OK.");
        Save(settings, outDir, $"settings-first-run-{suffix}.png");
        settings.Close();

        var serverUpdate = new ServerUpdateWindow(null, "1.0.52", "1.0.58");
        serverUpdate.Show();
        Save(serverUpdate, outDir, $"server-update-{suffix}.png");
        serverUpdate.ShowBusy("Installing and restarting the server");
        Save(serverUpdate, outDir, $"server-update-busy-{suffix}.png");
        serverUpdate.ShowResult(new ServerUpdateResult(true, "ok", "1.0.58", CanUpdateItself: true));
        Save(serverUpdate, outDir, $"server-update-done-{suffix}.png");
        serverUpdate.ShowResult(new ServerUpdateResult(false, "This server can't update itself yet.", "1.0.52", CanUpdateItself: false));
        Save(serverUpdate, outDir, $"server-update-manual-{suffix}.png");
        serverUpdate.Close();
    }

    private static void Save(Avalonia.Controls.Window window, string? outDir, string name)
    {
        // Let pages and rows finish fading in: animations follow the real clock, even headless.
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Thread.Sleep(420);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        frame!.Save(Path.Combine(outDir, name));
    }

    [Fact]
    public void PrivateFileSecretsAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return; // the file fallback exists only for Linux/macOS; Windows uses DPAPI
        using var dir = new TempDir("secret");
        var path = Path.Combine(dir.Path, "sub", "token");
        var store = new PrivateFileSecrets(path);
        var marker = store.Protect("a-very-secret-token-value");
        Assert.Equal("file:v1", marker);
        Assert.Equal("a-very-secret-token-value", store.Unprotect(marker));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
