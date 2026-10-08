using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pairnets.Client.Themes;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

/// <summary>
/// Renders the Windows windows (light and dark) to PNG files, with the same sample data as the
/// Mac/Linux screenshots (tests/Pairnets.Tests/Ui/DesktopUiTests.cs).
/// Usage: RenderScreens.Wpf &lt;output-dir&gt; [--print] [--scale=1] (--print also writes base64 to stdout for
/// log-only access; --scale sets the pixels per point, 1.5 by default).
/// </summary>
public static class Program
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
        public void AddComputer() { }
        public void ManageDevices() { }
        public void OpenNest() { }
        public void SignOut() { }
        public void ResetEverything() { }
        public void SetNotify(NoticeKind kind, bool on) { }
        public void OpenWindow(MainPage page) { }
        public void Quit() { }
    }

    /// <summary>Pixels per point in the PNG files.</summary>
    private static double Scale { get; set; } = 1.5;

    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "screens";
        var print = args.Contains("--print");
        if (args.FirstOrDefault(a => a.StartsWith("--scale=", StringComparison.Ordinal)) is { } scale)
            Scale = double.Parse(scale["--scale=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var dark in new[] { false, true })
        {
            ThemeManager.Apply(app, dark);
            RenderAll(outDir, dark ? "dark" : "light", print);
        }
        app.Shutdown();
        return 0;
    }

    private static void RenderAll(string outDir, string suffix, bool print)
    {
        var now = DateTimeOffset.Now;
        var utc = DateTimeOffset.UtcNow;
        var window = new MainWindow(new SampleActions()) { Width = 980, Height = 700 };
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
        // The same, plus a run of 18 holiday photos (a folder row in the Activity list).
        var batchFeed = new ActivityFeed();
        foreach (var item in Enumerable.Reverse(feed.Items))
            batchFeed.Add(item);
        for (var i = 0; i < 18; i++)
            batchFeed.Add(new ActivityItem(now.AddSeconds(-90 + i * 3), ActivityKind.Uploaded, $"Photos/summer/holiday-{410 + i:0000}.jpg", "Uploaded"));
        window.ShowActivity(feed.Items);
        window.ShowAttention(
        [
            new("LAPTOP-2 (Windows) wants to join", "Code KQ7M-4PXD. Check that it matches the code on that computer, then allow or deny it on your nest.",
                "Review in browser", () => { }),
            new("Conflict copy: report (conflict LAPTOP 2026-10-02 141509).docx",
                "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { }),
            new("Docs/Report.TXT", "Another file with the same name in different letter case exists on the server.", "Show in folder", () => { }),
        ]);
        const string folder = @"D:\Sync\Work";

        var server = new ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows"),
        ];
        window.ShowUpdate("Pairnets 1.0.58 is available", "You have 1.0.52. It takes about 10 seconds and Pairnets restarts by itself.", "Update now");
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1),
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 67_108_864, BytesTotal = 104_857_600,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20, BytesPerSecond = 4.9 * (1 << 20),
            LimitText = "Limited to 5 MB/s", Server = server, Devices = devices, NestUrl = "https://nest.example.com",
            Account = new AccountInfo("you@example.com", "google", utc.AddDays(-31)),
            Batch = SampleBatch(),
            Active =
            [
                new ActiveTransfer("Photos/summer/holiday-0412.jpg", "upload", 78, 100),
                new ActiveTransfer("Photos/summer/holiday-0413.jpg", "upload", 41, 100),
                new ActiveTransfer("Videos/presentation-final.mp4", "upload", 12, 100),
                new ActiveTransfer("Notes/meeting-2026-10-02.md", "upload", 95, 100),
            ],
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-syncing-{suffix}.png", print);
        window.ExpandFirstFolder();
        Save(window, outDir, $"windows-main-syncing-folder-{suffix}.png", print);
        window.OpenUpdateNotice();
        Save(window, outDir, $"windows-main-update-{suffix}.png", print);
        window.OpenAccountMenu();
        Save(window, outDir, $"windows-main-account-menu-{suffix}.png", print);
        window.Navigate(MainPage.Account);
        Save(window, outDir, $"windows-main-account-{suffix}.png", print);
        window.Navigate(MainPage.Overview);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, LastSyncAt = now.AddMinutes(-3),
            Text = "This sync would delete 37 files on the server (out of 120). Nothing was deleted.", Server = server, Devices = devices,
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-blocked-{suffix}.png", print);
        window.ShowAttentionTab(true);
        Save(window, outDir, $"windows-main-attention-{suffix}.png", print);

        window.ShowUpdate(null, string.Empty, string.Empty);
        window.ShowAttention([]);
        window.ShowAttentionTab(true);
        Save(window, outDir, $"windows-main-attention-empty-{suffix}.png", print);
        window.ShowAttentionTab(false);
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now, Server = server, Devices = devices }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-idle-{suffix}.png", print);
        if (window.MapOtherText != "LAPTOP: Online")
            throw new InvalidOperationException("The overview's picture shows " + window.MapOtherText);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-4), Server = server, Devices = devices,
            WaitingFor = new PeerWait("LAPTOP", 340, 212),
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-waiting-{suffix}.png", print);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-1), Server = server,
            Devices = [new("DESKTOP", utc.AddDays(-30), utc, true), new("LAPTOP", utc.AddHours(-3).AddDays(-30), utc.AddHours(-3), false, "1.0.58", "Windows")],
        }, folder, "DESKTOP");
        window.ShowActivity(batchFeed.Items);
        window.Navigate(MainPage.Activity);
        Save(window, outDir, $"windows-main-activity-{suffix}.png", print);
        window.ShowActivity(feed.Items);

        window.Navigate(MainPage.Devices);
        Save(window, outDir, $"windows-main-devices-{suffix}.png", print);

        window.Navigate(MainPage.History);
        window.LoadHistoryAsync().GetAwaiter().GetResult();
        window.SelectHistoryRow(0);
        Save(window, outDir, $"windows-main-history-{suffix}.png", print);

        var settingsPage = new SettingsView(new ClientSettings { ServerUrl = "https://sync.example.com/", Folder = folder, DeviceName = "DESKTOP", DeviceId = "id" },
            new NoProtector(), serverVersionText: "Server 1.0.58");
        window.ShowSettingsPage(settingsPage);
        Save(window, outDir, $"windows-main-settings-{suffix}.png", print);
        settingsPage.ScrollToEnd();
        Save(window, outDir, $"windows-main-settings-start-over-{suffix}.png", print);
        window.AllowClose = true;
        window.Close();

        var panel = new TrayPanel(new SampleActions());
        Place(panel);
        panel.ShowActivity(feed.Items);
        panel.ShowAttention(2);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1), Server = server, Devices = devices,
            CurrentPath = "Photos/summer/holiday-0412.jpg", Operation = "download", FilesDone = 12, FilesTotal = 30,
            PassBytesDone = 96L << 20, PassBytesTotal = 240L << 20, BytesPerSecond = 8.2 * (1 << 20),
            Active = [new ActiveTransfer("Photos/summer/holiday-0412.jpg", "download", 50, 100)],
            HeardFrom = new Dictionary<string, DateTimeOffset> { ["LAPTOP"] = utc },
        }, "DESKTOP");
        Save(panel, outDir, $"windows-tray-panel-{suffix}.png", print);
        panel.ShowAttention(0);
        panel.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now, Server = server,
            Devices = [new("DESKTOP", utc.AddDays(-30), utc, true), new("LAPTOP", utc.AddHours(-3).AddDays(-30), utc.AddHours(-3), false, "1.0.58", "Windows")],
        }, "DESKTOP");
        Save(panel, outDir, $"windows-tray-panel-idle-{suffix}.png", print);
        panel.Close();

        var signIn = new SettingsWindow(new ClientSettings { DeviceName = "DESKTOP" }, new NoProtector());
        Place(signIn);
        signIn.SignIn!.ShowAddress("nest.pairnets.app", new NestCheck(NestCheckStatus.Found, new Uri("https://nest.pairnets.app/"),
            new ServerHello("Pairnets", 1, "1.0.80", "https://nest.pairnets.app", true, true, new SignInMethods(true, true, true, true)),
            "Found it (Pairnets server 1.0.80)"));
        Save(signIn, outDir, $"windows-sign-in-welcome-{suffix}.png", print);
        signIn.SignIn.ShowAddress("nest.pairnets.app", new NestCheck(NestCheckStatus.Unreachable, new Uri("https://nest.pairnets.app/"), null,
            "Can't reach it. Check the name, and that this computer is online."));
        Save(signIn, outDir, $"windows-sign-in-unreachable-{suffix}.png", print);
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Waiting, "KQ7M-4PXD", "https://nest.pairnets.app/link?code=KQ7M-4PXD",
            DateTimeOffset.UtcNow.AddMinutes(9).AddSeconds(41)), openBrowser: false);
        Save(signIn, outDir, $"windows-sign-in-waiting-{suffix}.png", print);
        signIn.SignIn.ShowPairing(new PairingState(PairingStage.Denied, "KQ7M-4PXD", "https://nest.pairnets.app/link?code=KQ7M-4PXD"), openBrowser: false);
        Save(signIn, outDir, $"windows-sign-in-denied-{suffix}.png", print);
        signIn.SignIn.ShowFolderStep(new DeviceKeyGrant("id", "DESKTOP", "unused"), new Uri("https://nest.pairnets.app/"));
        signIn.SignIn.Folder = @"D:\Work";
        Save(signIn, outDir, $"windows-sign-in-folder-{suffix}.png", print);
        signIn.Close();

        var serverUpdate = new ServerUpdateWindow(null, "1.0.52", "1.0.58");
        Place(serverUpdate);
        Save(serverUpdate, outDir, $"windows-server-update-{suffix}.png", print);
        serverUpdate.ShowBusy("Installing and restarting the server");
        Save(serverUpdate, outDir, $"windows-server-update-busy-{suffix}.png", print);
        serverUpdate.ShowResult(new ServerUpdateResult(true, "ok", "1.0.58", CanUpdateItself: true));
        Save(serverUpdate, outDir, $"windows-server-update-done-{suffix}.png", print);
        serverUpdate.ShowResult(new ServerUpdateResult(false, "This server can't update itself yet.", "1.0.52", CanUpdateItself: false));
        Save(serverUpdate, outDir, $"windows-server-update-manual-{suffix}.png", print);
        serverUpdate.Close();
    }

    /// <summary>The sync behind the "syncing" screenshot: 18 holiday photos (2 done, 2 moving) and two single files.</summary>
    private static IReadOnlyList<BatchFile> SampleBatch()
    {
        var files = new List<BatchFile>();
        for (var i = 0; i < 18; i++)
        {
            var path = $"Photos/summer/holiday-{410 + i:0000}.jpg";
            files.Add(i switch
            {
                < 2 => new BatchFile(path, "upload", BatchFileState.Done),
                2 => new BatchFile(path, "upload", BatchFileState.Moving, 78),
                3 => new BatchFile(path, "upload", BatchFileState.Moving, 41),
                _ => new BatchFile(path, "upload", BatchFileState.Waiting),
            });
        }
        files.Add(new BatchFile("Videos/presentation-final.mp4", "upload", BatchFileState.Moving, 12));
        files.Add(new BatchFile("Notes/meeting-2026-10-02.md", "upload", BatchFileState.Moving, 95));
        return files;
    }

    private static void Place(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        // Windows counts the title bar and border in Width and Height, the Mac/Linux app does not: grow the
        // window so its inside has the size it asks for, and the pictures match the other app's.
        window.UpdateLayout();
        if (window.WindowStyle != WindowStyle.None && window.Content is FrameworkElement content)
        {
            window.Width = 2 * window.Width - (content.ActualWidth + content.Margin.Left + content.Margin.Right);
            if (window.SizeToContent == SizeToContent.Manual)
                window.Height = 2 * window.Height - (content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            window.UpdateLayout();
        }
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

    private static void Save(Window window, string outDir, string name, bool print)
    {
        Settle(window);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        // The bitmap's DPI does the scaling. The content is drawn as itself, at its margin (as a brush, a shadow
        // around it would shift it), over the window's background.
        var scale = Scale;
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(background);
        bitmap.Render(content);
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
