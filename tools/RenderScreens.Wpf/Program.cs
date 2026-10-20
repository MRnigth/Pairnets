using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pairnets.Client.Tests;
using Pairnets.Client.Themes;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

/// <summary>
/// Renders the Windows main and settings windows (light and dark) with sample data to PNG files.
/// Usage: RenderScreens.Wpf &lt;output-dir&gt; [--print] (--print also writes base64 to stdout for log-only access).
/// </summary>
public static partial class Program
{
    private sealed class NoProtector : ISecretProtector
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => protectedText;
    }

    /// <summary>Sample files and versions for the History page.</summary>
    private sealed class SampleHistory : IHistorySource
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public Task<IReadOnlyList<ServerFile>> GetServerFilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerFile>>(
        [
            new("Projects/2026/budget-draft.xlsx", true, _now.AddHours(-2), 0),
            new("Photos/summer/IMG_2041.jpg", true, _now.AddDays(-1).AddHours(-3), 0),
            new("Notes/old-ideas.md", true, _now.AddDays(-5), 0),
            new("Archive/2024/taxes.pdf", true, _now.AddDays(-12), 0),
            new("Projects/report.docx", false, _now.AddMinutes(-20), 482_304),
        ]);

        public Task<IReadOnlyList<HistoryVersion>> GetVersionsAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<HistoryVersion>>(
        [
            new("20261006T100000000Z-c0ffee00", _now.AddHours(-2), 48_220, "c0ffee00"),
            new("20261004T100000000Z-be5eda7a", _now.AddDays(-2).AddHours(-4), 47_100, "be5eda7a"),
            new("20260929T100000000Z-0ddba11a", _now.AddDays(-7), 39_870, "0ddba11a"),
        ]);

        public Task<string?> RestoreVersionAsync(string path, HistoryVersion version, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// <summary>The windows' buttons do nothing here; the History page reads <see cref="SampleHistory"/>.</summary>
    private sealed class SampleActions : ITrayActions
    {
        public IHistorySource? History { get; } = new SampleHistory();
        public void FixBlocked() { }
        public void SyncNow() { }
        public void TogglePause() { }
        public void OpenFolder() { }
        public void ShowSettings() { }
        public void ViewLog() { }
        public void UpdateNow() { }
        public void DismissUpdate() { }
        public void DownloadNow() { }
        public void UpdateServer() { }
        public void ReportBug() { }
        public void RevealFile(string syncPath) { }
        public void RefreshDevices() { }
        public Task<bool> SetDeviceIcon(string id, string icon) => Task.FromResult(true);
        public void AddComputer() { }
        public void ManageDevices() { }
        public void OpenWindow(MainPage page) { }
        public void Quit() { }
    }

    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "screens";
        var print = args.Contains("--print");
        Directory.CreateDirectory(outDir);
        // On a desktop of its own, so the windows it shows never appear on the screen.
        HiddenDesktop.Run("Pairnets screenshots", () =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var dark in new[] { false, true })
            {
                ThemeManager.Apply(app, dark);
                if (args.Contains("--stage3"))
                    RenderStage3(outDir, dark ? "dark" : "light", print);
                else if (args.Contains("--stage6"))
                    Stage6.Render(outDir, dark ? "dark" : "light", print);
                else if (args.Contains("--stage4"))
                    RenderStage4(outDir, dark ? "dark" : "light", print);
                else
                    RenderAll(outDir, dark ? "dark" : "light", print);
            }
            app.Shutdown();
        }).GetAwaiter().GetResult();
        return 0;
    }

    // ------------------------------------------------------------------ stage 4: the pages (Activity ... Account)

    private sealed class Stage4History : IHistorySource
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public Task<IReadOnlyList<ServerFile>> GetServerFilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerFile>>(
        [
            new("Drafts/old-draft.docx", true, _now.AddHours(-5), 0),
            new("Photos/Holiday 2026/holiday-0388.jpg", true, _now.AddHours(-7), 0),
            new("Notes/meeting-2026-09-18.md", true, _now.AddDays(-1).AddHours(-1), 0),
            new("Finance/budget-2025.xlsx", true, _now.AddDays(-1).AddHours(-6), 0),
            new("Scans/scan-0042.pdf", true, _now.AddDays(-4), 0),
            new("Talks/presentation-draft.mp4", true, _now.AddDays(-7), 0),
            new("Reports/report-q2.xlsx", true, _now.AddDays(-19), 0),
            new("Notes/notes-old.txt", true, _now.AddDays(-27), 0),
            new("Reports/report-q3.xlsx", false, _now.AddMinutes(-30), 2_516_582),
            new("Photos/Holiday 2026/holiday-0412.jpg", false, _now.AddMinutes(-44), 4_404_019),
            new("Finance/budget.xlsx", false, _now.AddMinutes(-55), 48_220),
            new("Notes/meeting-2026-10-02.md", false, _now.AddHours(-1), 3_100),
            new("Talks/presentation-final.mp4", false, _now.AddDays(-1), 860_000_000),
            new("Drafts/plan-2027.docx", false, _now.AddDays(-1).AddHours(-2), 91_000),
            new("Finance/invoice-0931.pdf", false, _now.AddDays(-2), 221_000),
            new("Notes/ideas.md", false, _now.AddDays(-3), 1_204),
        ]);

        public Task<IReadOnlyList<HistoryVersion>> GetVersionsAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<HistoryVersion>>(
        [
            new("20261006T100000000Z-c0ffee00", _now.AddHours(-2), 2_400_000, "c0ffee00"),
            new("20261004T100000000Z-be5eda7a", _now.AddDays(-1).AddHours(-4), 2_200_000, "be5eda7a"),
            new("20261001T100000000Z-0ddba11a", _now.AddDays(-4), 1_900_000, "0ddba11a"),
            new("20260929T100000000Z-aaa11a11", _now.AddDays(-6), 1_200_000, "aaa11a11"),
        ]);

        public Task<string?> RestoreVersionAsync(string path, HistoryVersion version, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class Stage4Actions : ITrayActions
    {
        public IHistorySource? History { get; } = new Stage4History();
        public void FixBlocked() { }
        public void SyncNow() { }
        public void TogglePause() { }
        public void OpenFolder() { }
        public void ShowSettings() { }
        public void ViewLog() { }
        public void UpdateNow() { }
        public void DismissUpdate() { }
        public void DownloadNow() { }
        public void UpdateServer() { }
        public void ReportBug() { }
        public void RevealFile(string syncPath) { }
        public void RefreshDevices() { }
        public Task<bool> SetDeviceIcon(string id, string icon) => Task.FromResult(true);
        public void AddComputer() { }
        public void ManageDevices() { }
        public void OpenWindow(MainPage page) { }
        public void Quit() { }
    }

    private static void RenderStage4(string outDir, string suffix, bool print)
    {
        var now = DateTimeOffset.Now;
        var utc = DateTimeOffset.UtcNow;
        const string folder = @"D:\Sync\Work";
        var server = new ServerInfo("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.48", "Windows") { LastChange = utc.AddMinutes(-5) },
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.48", "Windows") { LastChange = utc.AddMinutes(-19) },
            new("MINI-PC", utc.AddDays(-30), utc.AddSeconds(-3), true, "1.0.48", "Linux") { LastChange = utc.AddMinutes(-30) },
            new("MEDIA-PC", utc.AddDays(-30), utc.AddDays(-1), false, "1.0.48", "Linux") { LastChange = utc.AddDays(-1) },
            new("OFFICE-PC", utc.AddDays(-30), utc.AddDays(-2), false, "1.0.48", "Windows") { LastChange = utc.AddDays(-2) },
            // Went away 20 minutes ago in the middle of a download: the one red "can't reach" (the others are just off).
            new("KITCHEN-PC", utc.AddDays(-30), utc.AddMinutes(-20), false, "1.0.48", "Windows") { LastChange = utc.AddDays(-3) },
        ];
        var status = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-1), Server = server, Devices = devices,
            NestUrl = "https://nest.example.com", DeviceIconsSupported = true,
            Account = new AccountInfo("alex@example.com", "google", utc.AddDays(-14)),
            JoinRequests = [new JoinRequest("RX4K-92TB", "PC-1", "Windows", utc.AddMinutes(-9))],
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(0, 4.1 * (1 << 20), 30, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
                ["KITCHEN-PC"] = new(new TransferReport(0, 2.0 * (1 << 20), 4, 12, 0, 0, Downloading: true), utc.AddMinutes(-20), 0, null),
            },
        };

        // The activity: a folder of 37 photos, single files, a conflict, a deletion; yesterday's too.
        var feed = new ActivityFeed(300);
        var yesterday = now.AddDays(-1);
        feed.Add(new ActivityItem(yesterday.AddHours(-3), ActivityKind.Uploaded, "Talks/presentation-final.mp4", "Uploaded"));
        for (var i = 0; i < 12; i++)
            feed.Add(new ActivityItem(yesterday.AddHours(-5).AddSeconds(i * 4), ActivityKind.Downloaded, $"Reports/report-{i + 1:00}.xlsx", "Downloaded"));
        feed.Add(new ActivityItem(now.AddHours(-3), ActivityKind.DeletedHere, "Drafts/old-draft.docx", "Deleted"));
        feed.Add(new ActivityItem(now.AddHours(-2), ActivityKind.Uploaded, "Notes/meeting-2026-10-02.md", "Uploaded"));
        feed.Add(new ActivityItem(now.AddHours(-1), ActivityKind.Conflict, "Finance/budget.xlsx", "Conflict"));
        feed.Add(new ActivityItem(now.AddMinutes(-50), ActivityKind.Downloaded, "Photos/Holiday 2026/holiday-0412.jpg", "Downloaded"));
        for (var i = 0; i < 37; i++)
            feed.Add(new ActivityItem(now.AddMinutes(-40).AddSeconds(i * 3), ActivityKind.Uploaded, $"Photos/Holiday 2026/holiday-{414 - i:0000}.jpg", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-15), ActivityKind.Uploaded, "Reports/report-q3.xlsx", "Uploaded"));

        var window = new MainWindow(new Stage4Actions()) { Width = 1200, Height = 860 };
        Place(window);
        window.ShowStatus(status, folder, "DESKTOP");
        window.ShowActivity(feed.Items);
        window.ShowAttention(
        [
            // The same words the real app shows (TrayController.BuildAttention and the nest's message for a name clash).
            new("Use this computer's own name?", new NameOffer("PC-1", "StudyPC").Detail, "Use StudyPC", () => { }, AttentionKind.Rename, SecondLabel: "Keep PC-1", Second: () => { }),
            new("PC-1 (Windows) wants to join", new JoinRequest("RX4K-92TB", "PC-1", "Windows", now.AddMinutes(-9)).Advice, "Review in browser", () => { }, AttentionKind.Join, "RX4K-92TB", now.AddMinutes(-9)),
            new("Conflict copy: budget (conflict LAPTOP 2026-10-02 141509).xlsx", "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { }, AttentionKind.Conflict, null, now.AddHours(-1)),
            new("Docs/Report.TXT", "'Docs/Report.TXT' differs only in letter case from the existing file 'Docs/report.txt'.", "Show in folder", () => { }, AttentionKind.NameClash),
        ]);

        window.Navigate(MainPage.Activity);
        window.ActivityPane.OpenFolder("Holiday 2026");
        Save(window, outDir, $"stage4-activity-{suffix}.png", print);
        window.ActivityPane.SearchText = "zzz-not-there";
        Save(window, outDir, $"stage4-activity-search-empty-{suffix}.png", print);
        window.ActivityPane.SearchText = string.Empty;

        window.Navigate(MainPage.Attention);
        Save(window, outDir, $"stage4-needs-{suffix}.png", print);

        window.Navigate(MainPage.History);
        window.LoadHistoryAsync().GetAwaiter().GetResult();
        Save(window, outDir, $"stage4-history-deleted-{suffix}.png", print);
        window.HistoryPane.ShowAllFiles();
        window.SelectHistoryRow(0);
        Settle(window, 900);
        Save(window, outDir, $"stage4-history-all-{suffix}.png", print);

        window.ShowStatus(status, folder, "DESKTOP");
        window.Navigate(MainPage.Devices);
        Save(window, outDir, $"stage4-devices-{suffix}.png", print);

        window.Navigate(MainPage.Account);
        Save(window, outDir, $"stage4-account-{suffix}.png", print);
        window.ShowStatus(status with { Account = null }, folder, "DESKTOP");
        Save(window, outDir, $"stage4-account-old-nest-{suffix}.png", print);

        var settings = new ClientSettings
        {
            ServerUrl = "https://nest.example.com/", Folder = folder, DeviceName = "DESKTOP", DeviceId = "id", AccountEmail = "alex@example.com",
            ExtraIgnore = ["*.bak", "node_modules/", "~backup/"], UploadLimitMBps = 5, StartWithWindows = true, CheckForUpdates = true, AutoUpdateServer = true, WaitForPeerBatches = true,
        };
        using var updates = new UpdateService(new UpdateChecker(new HttpClient(), new Uri("https://downloads.example.com/"), new Uri("https://releases.example.com/")),
            PairnetsInfo.ProductVersion, null);
        var page = new SettingsView(settings, new NoProtector(), updates, "Nest " + PairnetsInfo.ProductVersion);
        window.ShowSettingsPage(page);
        Save(window, outDir, $"stage4-settings-{suffix}.png", print);
        page.ScrollToEnd();
        Save(window, outDir, $"stage4-settings-start-over-{suffix}.png", print);

        // A window with nothing synced yet.
        window.Navigate(MainPage.Overview);
        window.AllowClose = true;
        window.Close();
        var empty = new MainWindow(new Stage4Actions()) { Width = 1200, Height = 860 };
        Place(empty);
        empty.ShowStatus(status, folder, "DESKTOP");
        empty.ShowActivity([]);
        empty.Navigate(MainPage.Activity);
        Settle(empty, 900);
        Save(empty, outDir, $"stage4-activity-empty-{suffix}.png", print);
        empty.AllowClose = true;
        empty.Close();
    }

    private static void RenderAll(string outDir, string suffix, bool print)
    {
        var now = DateTimeOffset.Now;
        var utc = DateTimeOffset.UtcNow;
        var window = new MainWindow(new SampleActions()) { Width = 1200, Height = 860 };
        Place(window);
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
        window.ShowAttention(
        [
            new("Conflict copy: report (conflict LAPTOP 2026-10-02 141509).docx",
                "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { }),
            new("Docs/Report.TXT", "Another file with the same name in different letter case exists on the server.", "Show in folder", () => { }),
        ]);
        const string folder = @"D:\Sync\Work";

        var server = new ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
        // Six computers, as the design draws the left side: three online, three not reachable.
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
            new("MINI-PC", utc.AddDays(-30), utc.AddSeconds(-3), true, "1.0.58", "Linux"),
            new("MEDIA-PC", utc.AddDays(-30), utc.AddDays(-2), false, "1.0.58", "Linux"),
            new("OFFICE-PC", utc.AddDays(-30), utc.AddDays(-3), false, "1.0.58", "Windows"),
            new("KITCHEN-PC", utc.AddDays(-30), utc.AddMinutes(-20), false, "1.0.58", "Windows"),
        ];
        window.ShowUpdate("Pairnets 1.0.58 is available", "You have 1.0.52. It takes about 10 seconds and Pairnets restarts by itself.", "Update now");
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1),
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 67_108_864, BytesTotal = 104_857_600,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20, BytesPerSecond = 4.9 * (1 << 20),
            LimitText = "Limited to 5 MB/s", Server = server, Devices = devices,
            UpBytesPerSecond = 4.9 * (1 << 20),
            // LAPTOP and MINI-PC are getting the files, at different speeds.
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(0, 4.1 * (1 << 20), 30, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
                ["MINI-PC"] = new(new TransferReport(0, 2.6 * (1 << 20), 12, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
            },
            Active =
            [
                new ActiveTransfer("Photos/summer/holiday-0412.jpg", "upload", 78, 100),
                new ActiveTransfer("Photos/summer/holiday-0413.jpg", "upload", 41, 100),
                new ActiveTransfer("Videos/presentation-final.mp4", "upload", 12, 100),
                new ActiveTransfer("Notes/meeting-2026-10-02.md", "upload", 95, 100),
            ],
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-syncing-{suffix}.png", print);
        window.SideFolded = true;
        Save(window, outDir, $"windows-main-folded-{suffix}.png", print);
        window.SideFolded = false;

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, LastSyncAt = now.AddMinutes(-3),
            Text = "This sync would delete 37 files on the server (out of 120). Nothing was deleted.", Server = server, Devices = devices,
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-blocked-{suffix}.png", print);
        window.ShowAttentionTab(true);
        Save(window, outDir, $"windows-main-attention-{suffix}.png", print);

        // A release Pairnets did not sign: the card says so and only offers to hide it.
        window.ShowAttentionTab(false);
        window.ShowUpdate("Pairnets 1.0.58 could not be installed", UpdateProblem.NotSigned.Reason, UpdateProblem.NotSigned.ButtonText);
        Save(window, outDir, $"windows-update-not-signed-{suffix}.png", print);

        window.ShowUpdate(null, string.Empty, string.Empty);
        window.ShowAttention([]);
        window.ShowAttentionTab(false);
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now, Server = server, Devices = devices }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-idle-{suffix}.png", print);
        if (window.MapOtherText != "LAPTOP: in sync")
            throw new InvalidOperationException("The overview's picture shows " + window.MapOtherText);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-4), Server = server, Devices = devices,
            WaitingFor = new PeerWait("LAPTOP", 340, 212),
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-waiting-{suffix}.png", print);

        // This computer can't reach the nest: the others show when they were last seen.
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Offline, Text = "Offline: timeout", LastSyncAt = now.AddMinutes(-70), Devices = devices }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-offline-{suffix}.png", print);

        // Saying what it does instead of a bare "Syncing" (two PCs: PC-1 here on Windows, PC-2 on Linux).
        foreach (var (name, state) in SyncFeedbackStates(server, utc, now))
        {
            window.ShowStatus(state, folder, "PC-1");
            Save(window, outDir, $"windows-main-{name}-{suffix}.png", print);
        }
        window.ShowStatus(SyncFeedbackStates(server, utc, now)[0].State, folder, "PC-1");
        window.Navigate(MainPage.Devices);
        Save(window, outDir, $"windows-main-devices-live-{suffix}.png", print);
        window.Navigate(MainPage.Overview);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-1), Server = server,
            Devices = [new("DESKTOP", utc.AddDays(-30), utc, true), new("LAPTOP", utc.AddHours(-3).AddDays(-30), utc.AddHours(-3), false, "1.0.58", "Windows")],
        }, folder, "DESKTOP");
        window.Navigate(MainPage.Activity);
        Save(window, outDir, $"windows-main-activity-{suffix}.png", print);

        window.Navigate(MainPage.Devices);
        Save(window, outDir, $"windows-main-devices-{suffix}.png", print);

        window.Navigate(MainPage.History);
        window.LoadHistoryAsync().GetAwaiter().GetResult();
        window.SelectHistoryRow(0);
        Save(window, outDir, $"windows-main-history-{suffix}.png", print);

        var settingsPage = new SettingsView(new ClientSettings { ServerUrl = "https://sync.example.com/", Folder = folder, DeviceName = "DESKTOP" },
            new NoProtector(), serverVersionText: "Nest 1.0.58");
        window.ShowSettingsPage(settingsPage);
        Save(window, outDir, $"windows-main-settings-{suffix}.png", print);
        settingsPage.ShowNestStorage(SampleStorage(current: "/mnt/data/pairnets"));
        Save(window, outDir, $"windows-main-settings-nest-storage-{suffix}.png", print);
        settingsPage.ShowNestStorage(null);
        settingsPage.ScrollToEnd();
        Save(window, outDir, $"windows-main-settings-start-over-{suffix}.png", print);
        window.AllowClose = true;
        window.Close();

        var panel = new TrayPanel(new SampleActions());
        Place(panel);
        // Without the shadow and its margin, so the picture is the panel itself (a margin shifts what the brush below copies).
        var trayShell = (FrameworkElement)panel.Content;
        trayShell.Effect = null;
        trayShell.Margin = new Thickness(0);
        panel.ShowActivity(feed.Items);
        panel.ShowAttention(2);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1), Server = server, Devices = devices,
            CurrentPath = "Photos/summer/holiday-0412.jpg", Operation = "download", FilesDone = 12, FilesTotal = 30,
            PassBytesDone = 96L << 20, PassBytesTotal = 240L << 20, BytesPerSecond = 8.2 * (1 << 20),
            Active = [new ActiveTransfer("Photos/summer/holiday-0412.jpg", "download", 50, 100)],
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(2.4 * (1 << 20), 0, 4, 30, 0, 0), DateTimeOffset.UtcNow, 0, null),
                // Went away 20 minutes ago in the middle of a download: the red row (the others are the grey "off" row).
                ["KITCHEN-PC"] = new(new TransferReport(0, 2.0 * (1 << 20), 4, 12, 0, 0, Downloading: true), utc.AddMinutes(-20), 0, null),
            },
        }, "DESKTOP");
        Save(panel, outDir, $"windows-tray-panel-{suffix}.png", print);
        foreach (var (name, state) in SyncFeedbackStates(server, utc, now))
        {
            panel.ShowStatus(state, "PC-1");
            Save(panel, outDir, $"windows-tray-panel-{name}-{suffix}.png", print);
        }

        // The three designed states of the tray panel: six computers, three of them not reachable.
        var trayFeed = new ActivityFeed();
        trayFeed.Add(new ActivityItem(now.AddMinutes(-12), ActivityKind.Downloaded, "Photos/holiday-0412.jpg", "Downloaded"));
        trayFeed.Add(new ActivityItem(now.AddMinutes(-20), ActivityKind.Uploaded, "Notes/meeting-2026-10-02.md", "Uploaded"));
        trayFeed.Add(new ActivityItem(now.AddMinutes(-31), ActivityKind.Uploaded, "report-q3.xlsx", "Uploaded"));
        trayFeed.Add(new ActivityItem(now.AddMinutes(-42), ActivityKind.Downloaded, "Videos/presentation-final.mp4", "Downloaded"));
        panel.ShowActivity(trayFeed.Items);
        panel.ShowAttention(3);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1), Server = server, Devices = devices,
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 12, BytesTotal = 100,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20,
            BytesPerSecond = 4.9 * (1 << 20), UpBytesPerSecond = 4.9 * (1 << 20), MovingUp = true,
            Active = [new ActiveTransfer("Videos/presentation-final.mp4", "upload", 12, 100)],
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(0, 4.1 * (1 << 20), 30, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
                ["MINI-PC"] = new(new TransferReport(0, 2.6 * (1 << 20), 12, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
            },
        }, "DESKTOP");
        Save(panel, outDir, $"tray-syncing-{suffix}.png", print);
        panel.ShowAttention(0);
        panel.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-15), Server = server, Devices = devices }, "DESKTOP");
        Save(panel, outDir, $"tray-uptodate-{suffix}.png", print);
        panel.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Paused, Paused = true, Text = "Paused", LastSyncAt = now.AddMinutes(-30), Server = server, Devices = devices }, "DESKTOP");
        Save(panel, outDir, $"tray-paused-{suffix}.png", print);
        panel.Close();

        // Signing in with a Pairnets account: the first screen, the browser step, "no nest yet", the folder.
        var signIn = new SettingsWindow(new ClientSettings { DeviceName = "DESKTOP" }, new NoProtector());
        Place(signIn);
        Save(signIn, outDir, $"windows-sign-in-welcome-{suffix}.png", print);
        const string page = "https://sync.pairnets.app/app?code=KQ7M-4PXD&method=email";
        signIn.SignIn!.ShowAccount(new AccountSignInState(PairingStage.Waiting, "KQ7M-4PXD", page, DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(41)), openBrowser: false);
        Save(signIn, outDir, $"windows-sign-in-browser-{suffix}.png", print);
        signIn.SignIn.ShowAccount(new AccountSignInState(PairingStage.Waiting, "KQ7M-4PXD", page, DateTimeOffset.UtcNow.AddMinutes(30),
            AccountSignIn.NoNestMessage, NoNest: true, AccountEmail: "maria@example.com"), openBrowser: false);
        Save(signIn, outDir, $"windows-sign-in-no-nest-{suffix}.png", print);
        const string nestId = "nst_7f3k9q2m8v4c6x1t5r0p2n9b3d";
        signIn.SignIn.ShowFolderStep(new AccountSignInResult(Relay.AddressOf(nestId), nestId, "soro", new DeviceKeyGrant("id", "DESKTOP", "unused"), "maria@example.com"));
        signIn.SignIn.Folder = @"D:\Work";
        Save(signIn, outDir, $"windows-sign-in-folder-{suffix}.png", print);

        // "I run my own nest": its address, then approving this computer on it.
        signIn.SignIn.ShowAddress("nest.pairnets.app", new NestCheck(NestCheckStatus.Found, new Uri("https://nest.pairnets.app/"),
            new ServerHello("Pairnets", 1, "1.0.80", "https://nest.pairnets.app", true, true, new SignInMethods(true, true, true, true)),
            "Found it (Pairnets server 1.0.80)"));
        Save(signIn, outDir, $"windows-sign-in-own-nest-{suffix}.png", print);
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Waiting, "KQ7M-4PXD", "https://nest.pairnets.app/link?code=KQ7M-4PXD",
            DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(41)), openBrowser: false);
        Save(signIn, outDir, $"windows-sign-in-waiting-{suffix}.png", print);
        signIn.SignIn.ShowAddress("nest.example.com", new NestCheck(NestCheckStatus.Unreachable, null, null, "Can't reach nest.example.com. Check the spelling and your internet."));
        Save(signIn, outDir, $"windows-sign-in-cant-reach-{suffix}.png", print);
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Denied, "KQ7M-4PXD", "https://nest.pairnets.app/link?code=KQ7M-4PXD", null), openBrowser: false);
        Save(signIn, outDir, $"windows-sign-in-denied-{suffix}.png", print);
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Expired, "KQ7M-4PXD", "https://nest.pairnets.app/link?code=KQ7M-4PXD", null), openBrowser: false);
        Save(signIn, outDir, $"windows-sign-in-expired-{suffix}.png", print);
        signIn.Close();

        var serverUpdate = new ServerUpdateWindow(null, "1.0.52", "1.0.58", nestName: "nest.example.com");
        Place(serverUpdate);
        Save(serverUpdate, outDir, $"windows-server-update-{suffix}.png", print);
        serverUpdate.ShowBusy("Installing and restarting the server");
        Save(serverUpdate, outDir, $"windows-server-update-busy-{suffix}.png", print);
        serverUpdate.ShowResult(new ServerUpdateResult(true, "ok", "1.0.58", CanUpdateItself: true));
        Save(serverUpdate, outDir, $"windows-server-update-done-{suffix}.png", print);
        serverUpdate.ShowResult(new ServerUpdateResult(false, "This server can't update itself yet.", "1.0.52", CanUpdateItself: false));
        Save(serverUpdate, outDir, $"windows-server-update-manual-{suffix}.png", print);
        serverUpdate.Close();

        // Settings > Your nest > "Change…": the nest's drives, sure?, moving, moved, not moved.
        var place = new NestPlaceWindow(null, SampleStorage());
        Place(place);
        Save(place, outDir, $"windows-nest-place-drives-{suffix}.png", print);
        place.Flow.Choose("/mnt/data/pairnets");
        place.Render();
        Save(place, outDir, $"windows-nest-place-confirm-{suffix}.png", print);
        place.Flow.Asking();
        place.Flow.Seen(SampleStorage(new StorageMoveStatus(StorageMoveStatus.Running, 40, "Copying your files to the new drive")));
        place.Render();
        Save(place, outDir, $"windows-nest-place-moving-{suffix}.png", print);
        place.Flow.Seen(SampleStorage(new StorageMoveStatus(StorageMoveStatus.Succeeded, 100, "Done.", "/var/lib/pairnets-before-move-2026-10-20-120000"), "/mnt/data/pairnets"));
        place.Render();
        Save(place, outDir, $"windows-nest-place-done-{suffix}.png", print);
        place.Close();
        var failed = new NestPlaceWindow(null, SampleStorage());
        Place(failed);
        failed.Flow.Choose("/mnt/data/pairnets");
        failed.Flow.Asking();
        failed.Flow.Seen(SampleStorage(new StorageMoveStatus(StorageMoveStatus.Failed, 0, "That drive can't be used: /mnt/data is read-only. Nothing was changed.")));
        failed.Render();
        Save(failed, outDir, $"windows-nest-place-failed-{suffix}.png", print);
        failed.Close();

        var bugText = string.Join("\n", "Pairnets bug report", "Created:   2026-10-11 10:15:00", "App:       Windows app 1.0.49",
            "Nest:      nest.example.com (1.0.48)", "Computer:  DESKTOP", @"Folder:    D:\Sync\Work", "Status:    Up to date, last sync 10:14", "",
            "## Last log lines", "10:14:01 INF Uploaded report-q3.xlsx (1.2 MB)", "10:14:02 INF Up to date");
        var bugReport = new BugReportWindow(() => Task.FromResult(bugText), _ => { }, logsDir: Path.Combine(Path.GetTempPath(), "pairnets-bug-shots"));
        Place(bugReport);
        Save(bugReport, outDir, $"windows-bug-report-{suffix}.png", print);
        bugReport.Close();

        Stage6.Render(outDir, suffix, print);
    }

    /// <summary>
    /// The states asked for after the first big sync (two PCs on one home address, through sync.pairnets.app): both
    /// computers' real speeds, checking files, waiting for the other's batch, slowed down by the server, paused. The
    /// Mac/Linux screenshots (DesktopUiTests) show the same, from PC-2's side.
    /// </summary>
    private static List<(string Name, StatusSnapshot State)> SyncFeedbackStates(ServerInfo server, DateTimeOffset utc, DateTimeOffset now)
    {
        const double MB = 1 << 20;
        IReadOnlyList<DeviceInfo> pcs =
        [
            new("PC-1", utc.AddDays(-2), utc, true, "1.0.90", "Windows"),
            new("PC-2", utc.AddDays(-2), utc, true, "1.0.90", "Linux"),
        ];
        var basis = StatusSnapshot.Initial with { LastSyncAt = now.AddMinutes(-9), Server = server, Devices = pcs, IsRelay = true };
        return
        [
            ("live-speeds", basis with
            {
                Status = RunnerStatus.Syncing, Text = "Syncing", Stage = SyncStage.Transferring,
                CurrentPath = "Photos/2019/IMG_4410.jpg", Operation = "upload", FilesDone = 37_002, FilesTotal = 38_206,
                PassBytesDone = 98L << 30, PassBytesTotal = 114L << 30, BytesPerSecond = 3.1 * MB, UpBytesPerSecond = 3.1 * MB,
                Active =
                [
                    new ActiveTransfer("Photos/2019/IMG_4410.jpg", "upload", 64, 100),
                    new ActiveTransfer("Photos/2019/IMG_4411.jpg", "upload", 22, 100),
                    new ActiveTransfer("Photos/2019/IMG_4412.jpg", "upload", 90, 100),
                    new ActiveTransfer("Photos/2019/IMG_4413.jpg", "upload", 5, 100),
                ],
                Peers = new Dictionary<string, PeerActivity> { ["PC-2"] = new(new TransferReport(0, 4.2 * MB, 212, 640, 0, 0), DateTimeOffset.UtcNow, 0, null) },
            }),
            ("checking", basis with { Status = RunnerStatus.Syncing, Text = "Syncing", Stage = SyncStage.Checking, CheckedFiles = 12_000, CheckTotal = 38_206 }),
            ("waiting-live", basis with
            {
                Status = RunnerStatus.Idle, Text = "Up to date", WaitingFor = new PeerWait("PC-2", 38_206, 360),
                Peers = new Dictionary<string, PeerActivity> { ["PC-2"] = new(new TransferReport(3.1 * MB, 0, 383, 38_206, 1L << 30, 114L << 30), DateTimeOffset.UtcNow, 0, null) },
            }),
            ("slowed", basis with
            {
                Status = RunnerStatus.Syncing, Text = "Syncing", Stage = SyncStage.Transferring, CurrentPath = "Notes/2026/list.md", Operation = "upload",
                FilesDone = 383, FilesTotal = 38_206, PassBytesDone = 1L << 30, PassBytesTotal = 114L << 30,
                Active = [new ActiveTransfer("Notes/2026/list.md", "upload", 0, 100), new ActiveTransfer("Notes/2026/plan.md", "upload", 0, 100)],
                SlowedUntil = DateTimeOffset.UtcNow.AddSeconds(30.5), SlowedBy = "sync.pairnets.app",
            }),
            ("paused", basis with { Status = RunnerStatus.Paused, Text = "Paused", Paused = true }),
        ];
    }

    /// <summary>A Linux nest's drives as its root helper lists them: the main disk, a data drive, and a stick it can't use.</summary>
    private static NestStorage SampleStorage(StorageMoveStatus? status = null, string current = "/var/lib/pairnets") =>
        new(true, current, "/var/lib/pairnets", current == "/var/lib/pairnets", false, 3_960_000_000_000, 4_400_000_000_000,
        [
            new("/", "Main disk", "ext4", 182_000_000_000, 250_000_000_000, "/var/lib/pairnets", null, true),
            new("/mnt/data", "data", "ext4", 3_960_000_000_000, 4_400_000_000_000, "/mnt/data/pairnets"),
            new("/media/stick", "STICK", "vfat", 12_000_000_000, 32_000_000_000, "/media/stick/pairnets", "this drive has no Linux file permissions (vfat); format it as ext4 to use it"),
        ], DateTimeOffset.UtcNow, status);

    internal static void Place(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
    }

    /// <summary>Runs the dispatcher (and so the animations) for a moment: pages and rows fade in.</summary>
    private static void Settle(Window window, int milliseconds = 450)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds),
            System.Windows.Threading.DispatcherPriority.Background, (sender, _) =>
            {
                ((System.Windows.Threading.DispatcherTimer)sender!).Stop();
                frame.Continue = false;
            }, window.Dispatcher);
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    internal static void Save(Window window, string outDir, string name, bool print)
    {
        Settle(window);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        // The bitmap's DPI does the 1.5× (no extra transform), and the brush shows the content's whole layout box, empty
        // edges included (by default a VisualBrush crops to what is drawn, which shifts a page with margins).
        const double scale = 1.5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight),
            }, null, new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(outDir, name);
        using (var file = File.Create(path))
            encoder.Save(file);
        Console.WriteLine($"Wrote {path}");
        if (print)
        {
            Console.WriteLine($"----- BEGIN {name} -----");
            Console.WriteLine(Convert.ToBase64String(File.ReadAllBytes(path)));
            Console.WriteLine($"----- END {name} -----");
        }
    }
}
