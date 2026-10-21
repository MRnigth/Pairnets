using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Platform;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

[assembly: AvaloniaTestApplication(typeof(Pairnets.Tests.Ui.HeadlessApp))]

namespace Pairnets.Tests.Ui;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Motion.Enabled = true; // the tests check which animations run, whatever this computer's motion setting is
        return AppBuilder.Configure<Pairnets.Desktop.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
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

/// <summary>Records what the windows (and the tray panel) asked for; the History page reads <see cref="SampleHistory"/>.</summary>
public sealed class SampleActions : ITrayActions
{
    public List<string> Calls { get; } = [];

    public SampleHistory Samples { get; } = new();

    /// <summary>Set to read History from somewhere else, such as a server that cannot be reached.</summary>
    public IHistorySource? HistoryFrom { get; set; }

    public IHistorySource? History => HistoryFrom ?? Samples;

    /// <summary>The nest's files for the Files page (null: not connected).</summary>
    public FakeNestFiles? NestFiles { get; set; } = new();

    public INestFiles? Files => NestFiles;

    public void OpenFile(string path) => Calls.Add("open-file:" + path);

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
    public Task<bool> SetDeviceIcon(string id, string icon)
    {
        Calls.Add($"icon:{id}:{icon}");
        return Task.FromResult(true);
    }

    public void AddComputer() => Calls.Add("add-computer");

    public void ManageDevices() => Calls.Add("manage-devices");

    public Dictionary<NoticeKind, bool> Notify { get; } = [];

    public bool Notifies(NoticeKind kind) => Notify.GetValueOrDefault(kind, true);

    public void SetNotifies(NoticeKind kind, bool on)
    {
        Notify[kind] = on;
        Calls.Add($"notify:{kind}:{on}");
    }

    public void SignOut() => Calls.Add("sign-out");

    public void SignInAgain() => Calls.Add("sign-in-again");

    public void OpenAccountSite() => Calls.Add("account-site");

    public void OpenWindow(MainPage page) => Calls.Add("open:" + page);

    public void Quit() => Calls.Add("quit");
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
                new Pairnets.Core.DeviceInfo("DESKTOP", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, true, "1.0.38", "Windows"),
                new Pairnets.Core.DeviceInfo("MacBook", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, true, "1.0.38", "macOS"),
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
        Assert.Equal("Uploading 3 files to your nest", window.HeadlineText); // what it does, not a bare "Syncing"
        Assert.True(window.TransferVisible);
        Assert.False(window.FixVisible);

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 12 }, null);
        Assert.Equal("Stopped to keep your files safe", window.HeadlineText);
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
        Assert.Equal("rise", window.GlyphMotion); // the badge's arrow rises while uploading
        Assert.Equal("rise", window.ArrowMotion);
        Assert.Equal(2, window.ActiveRows);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, CurrentPath = "c.bin", Operation = "download", FilesTotal = 3,
            Active = [new ActiveTransfer("c.bin", "download", 10, 100)],
        }, null);
        Assert.Equal("fall", window.GlyphMotion);
        Assert.Equal("fall", window.ArrowMotion);
        Assert.Equal(1, window.ActiveRows);

        // Both ways: the second arrow falls while the first rises.
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, CurrentPath = "c.bin", Operation = "download", FilesTotal = 3,
            Active = [new ActiveTransfer("a.bin", "upload", 10, 100), new ActiveTransfer("c.bin", "download", 10, 100)],
        }, null);
        Assert.Equal("rise", window.GlyphMotion);
        Assert.True(window.SecondGlyphFalls);

        // Checking for changes: a calm turn. Stalled: nothing moves.
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Syncing }, null);
        Assert.Equal("spin-slow", window.GlyphMotion);
        Assert.Equal("Syncing…", window.HeadlineText);
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, CurrentPath = "a.bin", Operation = "upload", FilesTotal = 1,
            Active = [new ActiveTransfer("a.bin", "upload", 10, 100)], StalledSince = DateTimeOffset.UtcNow.AddSeconds(-8),
        }, null);
        Assert.Equal("none", window.GlyphMotion);
        Assert.Equal("none", window.ArrowMotion);
        Assert.False(window.SecondGlyphFalls);
        Assert.Equal("Waiting for the network", window.HeadlineText);

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, WaitingFor = new PeerWait("DESKTOP", 340, 12) }, null);
        Assert.Equal("none", window.GlyphMotion);
        Assert.True(window.BadgePulses);
        window.AllowClose = true;
        window.Close();

        var unknownVersion = new ServerUpdateWindow(null, string.Empty, "1.0.58");
        unknownVersion.Show();
        Assert.Equal("Your nest should be updated", unknownVersion.HeadingText);
        unknownVersion.Close();

        var upToDate = new ServerUpdateWindow(null, "1.0.58", "1.0.58");
        upToDate.Show();
        Assert.Equal("Your nest is up to date", upToDate.HeadingText);
        Assert.Equal("S.Green", upToDate.BadgeKey); // nothing is wrong: no orange warning badge
        Assert.False(upToDate.Bobbing);
        Assert.False(upToDate.DetailsShown); // only in Debug mode
        upToDate.ShowResult(new ServerUpdateResult(true, "Already up to date (1.0.58).", "1.0.58", CanUpdateItself: true, AlreadyUpToDate: true));
        Assert.Equal("Your nest is up to date", upToDate.HeadingText);
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
        Assert.Contains("Not connected to your nest", noSession.ExplanationText);
        noSession.Close();

        using var reports = new TempDir("bug-reports");
        var bug = new BugReportWindow(() => Task.FromResult("Pairnets bug report\nline two"), _ => { }, afterError: true, saveDirectory: reports.Path);
        Assert.True(bug.BuildAsync().IsCompleted);
        Assert.Equal("Pairnets bug report\nline two", bug.ReportValue);
        Assert.NotNull(bug.SavedPath);
        Assert.Equal("Pairnets bug report\nline two", File.ReadAllText(bug.SavedPath!));
        Assert.False(bug.CrashBannerShown);

        // A crash not sent yet: the banner offers to send it; after sending, the chip says so.
        using var crashes = new TempDir("crash-send");
        CrashCards.Add(crashes.Path, SampleCrash());
        var withCrash = new BugReportWindow(() => Task.FromResult("report"), _ => { }, saveDirectory: reports.Path, crashesDir: crashes.Path,
            sendCrashes: (_, _) => Task.FromResult<string?>(null));
        Assert.True(withCrash.BuildAsync().IsCompleted);
        Assert.True(withCrash.CrashBannerShown);
        withCrash.AfterSend(true, 1);
        Assert.Equal("Crash report sent to Pairnets", withCrash.SentChipValue);

        // The send window shows exactly the card, and a failed send says why and keeps the window open.
        var send = new SendCrashWindow([SampleCrash()], _ => Task.FromResult<string?>("Couldn't reach Pairnets."));
        send.Show();
        Assert.Equal(SampleCrash().ToText(), send.PreviewValue);
        Assert.True(send.SendAsync().IsCompleted);
        Assert.False(send.Sent);
        Assert.Equal("Couldn't reach Pairnets.", send.ProblemValue);
        Assert.True(send.IsVisible);
        send.Close();

        var dialog = new ServerUpdateWindow(null, "1.0.52", "1.0.58");
        dialog.Show();
        Assert.Equal("This app is newer than your nest", dialog.HeadingText);
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
        Assert.Equal("Your nest is on 1.0.58", dialog.HeadingText);
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
        window.ShowSettingsPage(new SettingsView(new ClientSettings { ServerUrl = "http://example.invalid:5075/", Folder = "/tmp/x", DeviceName = "mac" }, null, autoStart: false));
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
        Assert.Equal("DESKTOP: online", window.MapOtherText);
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
        Assert.Equal("Everything is up to date", panel.HeadlineText);
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
    public void TheSetupWindowIsTheSignInAndNothingElse()
    {
        var window = new SettingsWindow(new ClientSettings(), null, autoStart: false);
        window.Show();
        Assert.Contains("sign in", window.Title);
        Assert.NotNull(window.SignIn);
        Assert.Same(window.SignIn, window.Host.Content); // no form for a server address and token behind it
        Assert.Equal(SignInStep.Account, window.SignIn.Step); // "Sign in to Pairnets" first; your own nest is behind a link
        Assert.True(window.SignIn.AccountStep.IsVisible);
        Assert.False(window.SignIn.WelcomeStep.IsVisible);
        window.Close();
    }

    /// <summary>
    /// Renders the windows (light and dark) to PNG files for documentation when PAIRNETS_SCREENSHOT_DIR
    /// is set (otherwise it just checks that frames can be rendered).
    /// </summary>
    [AvaloniaFact]
    public void RenderScreenshots()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            RenderAll(outDir, suffix);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static void RenderAll(string? outDir, string suffix)
    {
        Stage4Screens.Render(outDir, suffix);
        var now = DateTimeOffset.Now;
        var actions = new SampleActions();
        var window = new MainWindow(actions) { Width = 1200, Height = 860 };
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
            new("LAPTOP-2 (Windows) wants to join", new JoinRequest("KQ7M-4PXD", "LAPTOP-2", "Windows", DateTimeOffset.UtcNow).Advice,
                "Review in browser", () => { }),
            new("Conflict copy: report (conflict LAPTOP 2026-10-02 141509).docx",
                "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { }),
            new("Docs/Report.TXT", "Another file with the same name in different letter case exists on the server.", "Show in folder", () => { }),
        };
        window.ShowAttention(attention);

        var server = new Pairnets.Core.ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
        var utc = DateTimeOffset.UtcNow;
        IReadOnlyList<Pairnets.Core.DeviceInfo> devices =
        [
            new("MacBook", utc.AddDays(-30), utc, true, "1.0.58", "macOS"),
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
            new("MINI-PC", utc.AddDays(-30), utc.AddSeconds(-3), true, "1.0.58", "Linux"),
            new("MEDIA-PC", utc.AddDays(-30), utc.AddDays(-2), false, "1.0.58", "Linux"),
            new("OFFICE-PC", utc.AddDays(-30), utc.AddDays(-3), false, "1.0.58", "Windows"),
        ];
        window.ShowUpdate("Pairnets 1.0.58 is available", "You have 1.0.52. The update takes about 10 seconds and Pairnets restarts by itself.", "Update now");
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1),
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 67_108_864, BytesTotal = 104_857_600,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20, BytesPerSecond = 4.9 * (1 << 20),
            UpBytesPerSecond = 4.9 * (1 << 20), MyChangeVersion = 120,
            Peers = new Dictionary<string, PeerActivity>
            {
                ["DESKTOP"] = new(new Pairnets.Core.TransferReport(0, 3_400_000, 0, 0, 0, 0, Downloading: true), utc, 80, utc.AddMinutes(-2)),
                ["LAPTOP"] = new(new Pairnets.Core.TransferReport(0, 2.6 * (1 << 20), 12, 120, 0, 0, Downloading: true), utc, 80, utc.AddMinutes(-2)),
            },
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
        window.SideFolded = true;
        Save(window, outDir, $"main-window-folded-{suffix}.png");
        window.SideFolded = false;

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

        // This computer can't reach the nest: the others show when they were last seen.
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Offline, Text = "Offline: timeout", LastSyncAt = now.AddMinutes(-70), Devices = devices }, "/Users/me/Work", "MacBook");
        Save(window, outDir, $"main-window-offline-{suffix}.png");

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-4), Server = server, Devices = devices,
            WaitingFor = new Pairnets.Core.Sync.PeerWait("DESKTOP", 340, 212),
        }, "/Users/me/Work", "MacBook");
        Save(window, outDir, $"main-window-waiting-{suffix}.png");

        // Saying what it does instead of a bare "Syncing" (the owner's two PCs: PC-2 here on Linux, PC-1 on Windows).
        foreach (var (name, state) in SyncFeedbackStates(server, utc, now))
        {
            window.ShowStatus(state, "/home/me/Pairnets", "PC-2");
            Save(window, outDir, $"main-window-{name}-{suffix}.png");
        }
        window.ShowStatus(SyncFeedbackStates(server, utc, now)[0].State, "/home/me/Pairnets", "PC-2");
        window.Navigate(MainPage.Devices);
        Save(window, outDir, $"main-window-devices-live-{suffix}.png");
        window.Navigate(MainPage.Overview);

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

        var settingsPage = new SettingsView(new ClientSettings { ServerUrl = "https://sync.example.com/", Folder = "/Users/me/Work", DeviceName = "MacBook", DeviceId = "id" },
            null, autoStart: true, serverVersionText: "Nest 1.0.58");
        window.ShowSettingsPage(settingsPage);
        Save(window, outDir, $"main-window-settings-{suffix}.png");
        // A Linux nest that can move its files, and the Linux app's program folder.
        settingsPage.ShowNestStorage(Pairnets.Tests.Unit.NestPlaceFlowTests.Sample(current: "/mnt/data/pairnets"));
        settingsPage.ShowProgramFolder("~/.local/opt/pairnets");
        Save(window, outDir, $"main-window-settings-nest-storage-{suffix}.png");
        settingsPage.ShowNestStorage(null);
        settingsPage.ShowProgramFolder(null);
        settingsPage.ScrollToEnd();
        Save(window, outDir, $"main-window-settings-start-over-{suffix}.png");
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
            Peers = new Dictionary<string, PeerActivity> { ["DESKTOP"] = new(new Pairnets.Core.TransferReport(2.4 * (1 << 20), 0, 4, 30, 0, 0), DateTimeOffset.UtcNow, 0, null) },
        }, "MacBook");
        Save(panel, outDir, $"tray-panel-syncing-{suffix}.png");
        panel.ShowAttention(0);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now, Server = server,
            Devices = [new("MacBook", utc.AddDays(-30), utc, true), new("DESKTOP", utc.AddHours(-3).AddDays(-30), utc.AddHours(-3), false, "1.0.58", "Windows")],
        }, "MacBook");
        Save(panel, outDir, $"tray-panel-idle-{suffix}.png");
        foreach (var (name, state) in SyncFeedbackStates(server, utc, now))
        {
            panel.ShowStatus(state, "PC-2");
            Save(panel, outDir, $"tray-panel-{name}-{suffix}.png");
        }

        // The three designed states of the tray panel: six computers, three of them not reachable.
        IReadOnlyList<Pairnets.Core.DeviceInfo> sixComputers =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.58", "Linux"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
            new("MINI-PC", utc.AddDays(-30), utc.AddSeconds(-3), true, "1.0.58", "Linux"),
            new("MEDIA-PC", utc.AddDays(-30), utc.AddDays(-2), false, "1.0.58", "Linux"),
            new("OFFICE-PC", utc.AddDays(-30), utc.AddDays(-3), false, "1.0.58", "Windows"),
            new("KITCHEN-PC", utc.AddDays(-30), utc.AddMinutes(-20), false, "1.0.58", "Windows"),
        ];
        var trayFeed = new ActivityFeed();
        trayFeed.Add(new ActivityItem(now.AddMinutes(-12), ActivityKind.Downloaded, "Photos/holiday-0412.jpg", "Downloaded"));
        trayFeed.Add(new ActivityItem(now.AddMinutes(-20), ActivityKind.Uploaded, "Notes/meeting-2026-10-02.md", "Uploaded"));
        trayFeed.Add(new ActivityItem(now.AddMinutes(-31), ActivityKind.Uploaded, "report-q3.xlsx", "Uploaded"));
        trayFeed.Add(new ActivityItem(now.AddMinutes(-42), ActivityKind.Downloaded, "Videos/presentation-final.mp4", "Downloaded"));
        panel.ShowActivity(trayFeed.Items);
        panel.ShowAttention(3);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1), Server = server, Devices = sixComputers,
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 12, BytesTotal = 100,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20,
            BytesPerSecond = 4.9 * (1 << 20), UpBytesPerSecond = 4.9 * (1 << 20), MovingUp = true,
            Active = [new ActiveTransfer("Videos/presentation-final.mp4", "upload", 12, 100)],
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new Pairnets.Core.TransferReport(0, 4.1 * (1 << 20), 30, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
                ["MINI-PC"] = new(new Pairnets.Core.TransferReport(0, 2.6 * (1 << 20), 12, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
                // Went away 20 minutes ago in the middle of a download: the red row (the others are the grey "off" row).
                ["KITCHEN-PC"] = new(new Pairnets.Core.TransferReport(0, 2.0 * (1 << 20), 4, 12, 0, 0, Downloading: true), utc.AddMinutes(-20), 0, null),
            },
        }, "DESKTOP");
        Save(panel, outDir, $"tray-syncing-{suffix}.png");
        panel.ShowAttention(0);
        panel.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-15), Server = server, Devices = sixComputers }, "DESKTOP");
        Save(panel, outDir, $"tray-uptodate-{suffix}.png");
        panel.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Paused, Paused = true, Text = "Paused", LastSyncAt = now.AddMinutes(-30), Server = server, Devices = sixComputers }, "DESKTOP");
        Save(panel, outDir, $"tray-paused-{suffix}.png");
        panel.Close();

        // Signing in with a Pairnets account: the first screen, the browser step, "no nest yet", the folder.
        var signIn = new SettingsWindow(new ClientSettings { DeviceName = "MacBook" }, null, autoStart: true);
        signIn.Show();
        Save(signIn, outDir, $"sign-in-welcome-{suffix}.png");
        const string page = "https://sync.pairnets.app/app?code=KQ7M-4PXD&method=email";
        signIn.SignIn.ShowAccount(new AccountSignInState(PairingStage.Waiting, "KQ7M-4PXD", page, DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(41)), openBrowser: false);
        Save(signIn, outDir, $"sign-in-browser-{suffix}.png");
        signIn.SignIn.ShowAccount(new AccountSignInState(PairingStage.Waiting, "KQ7M-4PXD", page, DateTimeOffset.UtcNow.AddMinutes(30),
            AccountSignIn.NoNestMessage, NoNest: true, AccountEmail: "maria@example.com"), openBrowser: false);
        Save(signIn, outDir, $"sign-in-no-nest-{suffix}.png");
        signIn.SignIn.ShowFolderStep(SampleAccountResult("MacBook"));
        signIn.SignIn.Folder = "/Users/me/Work";
        Save(signIn, outDir, $"sign-in-folder-{suffix}.png");

        // "I run my own nest": its address (find it, found it, the browser as the only way in, no answer), then approving this computer on it.
        signIn.SignIn.ShowAddress(string.Empty, new NestCheck(NestCheckStatus.Empty, null, null, string.Empty));
        Save(signIn, outDir, $"sign-in-start-{suffix}.png");
        signIn.SignIn.ShowAddress("nest.example.com", new NestCheck(NestCheckStatus.Found, new Uri("https://nest.example.com/"),
            new ServerHello("Pairnets", 1, "1.0.80", "https://nest.example.com", true, true, new SignInMethods(true, true, true, true)),
            "Found it (Pairnets server 1.0.80)"));
        Save(signIn, outDir, $"sign-in-own-nest-{suffix}.png");
        signIn.SignIn.ShowAddress("nest.example.com", new NestCheck(NestCheckStatus.Found, new Uri("https://nest.example.com/"),
            new ServerHello("Pairnets", 1, "1.0.80", "https://nest.example.com", true, true, new SignInMethods(true, true, false, false)),
            "Found it (Pairnets server 1.0.80)"));
        Save(signIn, outDir, $"sign-in-browser-only-{suffix}.png");
        signIn.SignIn.ShowAddress("nest.example.com", new NestCheck(NestCheckStatus.Unreachable, new Uri("https://nest.example.com/"), null, "Can't reach it. Check the name, and that this computer is online."));
        Save(signIn, outDir, $"sign-in-unreachable-{suffix}.png");
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Waiting, "KQ7M-4PXD", "https://nest.example.com/link?code=KQ7M-4PXD",
            DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(41)), openBrowser: false);
        Save(signIn, outDir, $"sign-in-waiting-{suffix}.png");
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Denied, "KQ7M-4PXD", "https://nest.example.com/link?code=KQ7M-4PXD"), openBrowser: false);
        Save(signIn, outDir, $"sign-in-denied-{suffix}.png");
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Expired, "KQ7M-4PXD", "https://nest.example.com/link?code=KQ7M-4PXD"), openBrowser: false);
        Save(signIn, outDir, $"sign-in-expired-{suffix}.png");
        signIn.Close();

        // The nest update window: offer, busy, done, and the one-time setup.
        var serverUpdate = new ServerUpdateWindow(null, "1.0.48", "1.0.49", nestName: "nest.example.com", computerName: "DESKTOP");
        serverUpdate.Show();
        Save(serverUpdate, outDir, $"server-update-{suffix}.png");
        serverUpdate.ShowBusy("Installing and restarting the server");
        Save(serverUpdate, outDir, $"server-update-busy-{suffix}.png");
        serverUpdate.ShowResult(new ServerUpdateResult(true, "ok", "1.0.49", CanUpdateItself: true));
        Save(serverUpdate, outDir, $"server-update-done-{suffix}.png");
        serverUpdate.ShowResult(new ServerUpdateResult(false, "This nest can't update itself yet.", "1.0.48", CanUpdateItself: false));
        Save(serverUpdate, outDir, $"server-update-manual-{suffix}.png");
        serverUpdate.Close();

        // Settings > Your nest > "Change…": the nest's drives, sure?, moving, moved, not moved.
        var place = new NestPlaceWindow(null, Pairnets.Tests.Unit.NestPlaceFlowTests.Sample());
        place.Show();
        Save(place, outDir, $"nest-place-drives-{suffix}.png");
        place.Flow.Choose("/mnt/data/pairnets");
        place.Render();
        Save(place, outDir, $"nest-place-confirm-{suffix}.png");
        place.Flow.Asking();
        place.Flow.Seen(Pairnets.Tests.Unit.NestPlaceFlowTests.Sample(new StorageMoveStatus(StorageMoveStatus.Running, 40, "Copying your files to the new drive")));
        place.Render();
        Save(place, outDir, $"nest-place-moving-{suffix}.png");
        place.Flow.Seen(Pairnets.Tests.Unit.NestPlaceFlowTests.Sample(new StorageMoveStatus(StorageMoveStatus.Succeeded, 100, "Done.",
            "/var/lib/pairnets-before-move-2026-10-20-120000"), "/mnt/data/pairnets"));
        place.Render();
        Save(place, outDir, $"nest-place-done-{suffix}.png");
        place.Close();
        var failed = new NestPlaceWindow(null, Pairnets.Tests.Unit.NestPlaceFlowTests.Sample());
        failed.Show();
        failed.Flow.Choose("/mnt/data/pairnets");
        failed.Flow.Asking();
        failed.Flow.Seen(Pairnets.Tests.Unit.NestPlaceFlowTests.Sample(new StorageMoveStatus(StorageMoveStatus.Failed, 0, "That drive can't be used: /mnt/data is read-only. Nothing was changed.")));
        failed.Render();
        Save(failed, outDir, $"nest-place-failed-{suffix}.png");
        failed.Close();

        // The bug report window, with a sample report.
        var bugDir = Path.Combine(Path.GetTempPath(), "pairnets-bug-shots");
        var bug = new BugReportWindow(() => Task.FromResult(string.Join("\n",
            "Pairnets bug report", "Created:   2026-10-11 10:15:00", "App:       Windows app 1.0.49", "System:    Windows 11 (x64)",
            "Nest:      nest.example.com (1.0.48)", "Computer:  DESKTOP", @"Folder:    D:\Sync\Work", "Status:    Up to date, last sync 10:14", "",
            "## Last log lines", "10:13:58 INF Saw a change: report-q3.xlsx", "10:14:01 INF Uploaded report-q3.xlsx (1.2 MB)",
            "10:14:02 INF Downloaded holiday-0412.jpg from LAPTOP", "10:14:02 INF Pass finished: 3 up, 1 down, 0 deleted", "10:14:02 INF Up to date")),
            _ => { }, saveDirectory: bugDir);
        bug.Show();
        bug.BuildAsync().GetAwaiter().GetResult();
        Save(bug, outDir, $"bug-report-{suffix}.png");
        bug.Close();

        // "Send crash report…": the banner in Report a bug, and the window that shows what will be sent.
        var crashDir = Path.Combine(Path.GetTempPath(), "pairnets-crash-shots-" + suffix);
        if (Directory.Exists(crashDir))
            Directory.Delete(crashDir, true);
        CrashCards.Add(crashDir, SampleCrash());
        var withCrash = new BugReportWindow(() => Task.FromResult("Pairnets bug report\n\n## Crash cards (cleaned, kept on this computer; 1)\n" + SampleCrash().ToText()),
            _ => { }, saveDirectory: bugDir, crashesDir: crashDir, sendCrashes: (_, _) => Task.FromResult<string?>(null));
        withCrash.Show();
        withCrash.BuildAsync().GetAwaiter().GetResult();
        Save(withCrash, outDir, $"bug-report-crash-{suffix}.png");
        withCrash.Close();
        var sendCrash = new SendCrashWindow([SampleCrash()], _ => Task.FromResult<string?>(null)) { Note = "I renamed a folder while it was syncing." };
        sendCrash.Show();
        Save(sendCrash, outDir, $"send-crash-{suffix}.png");
        sendCrash.Close();
    }

    /// <summary>
    /// The states the owner asked to see after the first big sync (two PCs on one home address, through sync.pairnets.app):
    /// both computers' real speeds, checking files, waiting for PC-1's batch, slowed down by the server, paused.
    /// </summary>
    private static List<(string Name, StatusSnapshot State)> SyncFeedbackStates(Pairnets.Core.ServerInfo server, DateTimeOffset utc, DateTimeOffset now)
    {
        const double MB = 1 << 20;
        IReadOnlyList<Pairnets.Core.DeviceInfo> pcs =
        [
            new("PC-1", utc.AddDays(-2), utc, true, "1.0.90", "Windows"),
            new("PC-2", utc.AddDays(-2), utc, true, "1.0.90", "Linux"),
        ];
        var pc1Uploading = new Dictionary<string, PeerActivity>
        {
            ["PC-1"] = new(new Pairnets.Core.TransferReport(3.1 * MB, 0, 37_002, 38_206, 98L << 30, 114L << 30), DateTimeOffset.UtcNow, 0, null),
        };
        var basis = StatusSnapshot.Initial with { LastSyncAt = now.AddMinutes(-9), Server = server, Devices = pcs, IsRelay = true };
        var downloading = basis with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", Stage = SyncStage.Transferring,
            CurrentPath = "Photos/2019/IMG_4410.jpg", Operation = "download", FilesDone = 212, FilesTotal = 640,
            PassBytesDone = 1290L << 20, PassBytesTotal = 3900L << 20, BytesPerSecond = 4.2 * MB, DownBytesPerSecond = 4.2 * MB,
            Active =
            [
                new ActiveTransfer("Photos/2019/IMG_4410.jpg", "download", 64, 100),
                new ActiveTransfer("Photos/2019/IMG_4411.jpg", "download", 22, 100),
                new ActiveTransfer("Photos/2019/IMG_4412.jpg", "download", 90, 100),
                new ActiveTransfer("Photos/2019/IMG_4413.jpg", "download", 5, 100),
            ],
            Peers = new Dictionary<string, PeerActivity>
            {
                ["PC-1"] = new(new Pairnets.Core.TransferReport(3.1 * MB, 0, 37_002, 38_206, 98L << 30, 114L << 30), DateTimeOffset.UtcNow, 0, null),
            },
        };
        return
        [
            ("live-speeds", downloading),
            ("checking", basis with { Status = RunnerStatus.Syncing, Text = "Syncing", Stage = SyncStage.Checking, CheckedFiles = 12_000, CheckTotal = 38_206 }),
            ("waiting-live", basis with
            {
                Status = RunnerStatus.Idle, Text = "Up to date", WaitingFor = new PeerWait("PC-1", 38_206, 360),
                Peers = new Dictionary<string, PeerActivity> { ["PC-1"] = new(new Pairnets.Core.TransferReport(3.1 * MB, 0, 383, 38_206, 1L << 30, 114L << 30), DateTimeOffset.UtcNow, 0, null) },
            }),
            ("slowed", basis with
            {
                Status = RunnerStatus.Syncing, Text = "Syncing", Stage = SyncStage.Transferring, CurrentPath = "Notes/2026/list.md", Operation = "upload",
                FilesDone = 383, FilesTotal = 38_206, PassBytesDone = 1L << 30, PassBytesTotal = 114L << 30,
                Active = [new ActiveTransfer("Notes/2026/list.md", "upload", 0, 100), new ActiveTransfer("Notes/2026/plan.md", "upload", 0, 100)],
                SlowedUntil = DateTimeOffset.UtcNow.AddSeconds(30.5), SlowedBy = "sync.pairnets.app",
            }),
            ("paused", basis with { Status = RunnerStatus.Paused, Text = "Paused", Paused = true, Peers = pc1Uploading }),
        ];
    }

    /// <summary>What an account sign-in hands back for the screenshots: a computer on the account's nest "soro".</summary>
    private static AccountSignInResult SampleAccountResult(string name)
    {
        const string nestId = "nst_7f3k9q2m8v4c6x1t5r0p2n9b3d";
        return new AccountSignInResult(Relay.AddressOf(nestId), nestId, "soro", new DeviceKeyGrant("id", name, "unused"), "maria@example.com");
    }

    /// <summary>A cleaned crash card as Report a bug shows it.</summary>
    private static CrashCard SampleCrash() => new()
    {
        Id = "3f9a1c0e72b4", App = "Linux app", Version = "1.0.60", System = "Ubuntu 24.04.1 LTS (X64), .NET 8.0.10", Place = CrashPlace.Background,
        Error = "System.NullReferenceException", Message = "Object reference not set to an instance of an object.",
        Details = "System.NullReferenceException: Object reference not set to an instance of an object.\n   at Pairnets.Core.Sync.SyncRunner.ApplyRenameAsync(RemoteChange change)\n   at Pairnets.Core.Sync.SyncRunner.RunPassAsync(CancellationToken ct)",
        FirstSeen = new DateTimeOffset(2026, 10, 20, 9, 42, 0, TimeSpan.Zero), LastSeen = new DateTimeOffset(2026, 10, 21, 9, 42, 0, TimeSpan.Zero),
        Count = 2, Closed = true, Noticed = true,
    };

    internal static void Save(Avalonia.Controls.Window window, string? outDir, string name)
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

        ((ISecretProtector)store).Forget(marker); // "Reset this app" removes it
        Assert.False(File.Exists(path));
        ((ISecretProtector)store).Forget(marker); // already gone: no error
        ((ISecretProtector)store).Forget("keychain:v1"); // somebody else's marker: left alone
    }

    [AvaloniaFact]
    public void TheSettingsPageHasAResetButtonThatAsksTheController()
    {
        var view = new SettingsView(new ClientSettings { ServerUrl = "http://example.invalid:5075/", Folder = "/tmp/x", DeviceName = "mac" }, null, autoStart: false);
        var window = new Avalonia.Controls.Window { Content = view };
        window.Show();
        var asked = 0;
        view.ResetRequested += () => asked++;

        var button = view.ResetButton;

        Assert.NotNull(button);
        Assert.True(button.IsVisible); // also for a computer that is not signed in with its own key
        Assert.Contains("reset", button.Classes); // the red "cannot be undone" button
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(1, asked);
        window.Close();
    }

    [AvaloniaFact]
    public void TheSettingsPageLinksTheWebsitesHelpAndLegalPages()
    {
        var view = new SettingsView(new ClientSettings { ServerUrl = "http://example.invalid:5075/", Folder = "/tmp/x", DeviceName = "mac" }, null, autoStart: false);
        var window = new Avalonia.Controls.Window { Content = view };
        window.Show();
        var opened = new List<string>();
        view.OpenLinkRequested += opened.Add;

        Assert.Contains(Pairnets.Core.PairnetsInfo.ProductVersion, view.AboutText.Text);
        Assert.Contains("MIT licence", view.AboutText.Text);
        var expected = new (Avalonia.Controls.Button Link, string Url)[]
        {
            (view.DonateLink, "https://pairnets.app/donate"),
            (view.HelpLink, "https://pairnets.app/help/"),
            (view.FaqLink, "https://pairnets.app/faq/"),
            (view.ContactLink, "https://pairnets.app/contact/"),
            (view.PrivacyLink, "https://pairnets.app/privacy/"),
            (view.TermsLink, "https://pairnets.app/terms/"),
            (view.GuidelinesLink, "https://pairnets.app/guidelines/"),
            (view.CookiesLink, "https://pairnets.app/cookies/"),
            (view.SecurityLink, "https://pairnets.app/security/"),
            (view.LicensesLink, "https://pairnets.app/licenses/"),
            (view.DeleteAccountLink, "https://pairnets.app/delete-account/"),
        };
        Assert.Equal(expected.Length, view.AboutLinks.Children.Count); // a new link needs a line here too
        foreach (var (link, url) in expected)
        {
            Assert.True(link.IsVisible, link.Name);
            link.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.Equal(url, opened.Last());
        }
        Assert.Equal(expected.Length, opened.Count);
        window.Close();
    }
}
