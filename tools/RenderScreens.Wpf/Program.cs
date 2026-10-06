using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tether.Client.Themes;
using Tether.Client.Ui;
using Tether.Core;
using Tether.Core.Client;
using Tether.Core.Settings;
using Tether.Core.Sync;

namespace Tether.Tools;

/// <summary>
/// Renders the Windows main and settings windows (light and dark) with sample data to PNG files.
/// Usage: RenderScreens.Wpf &lt;output-dir&gt; [--print] (--print also writes base64 to stdout for log-only access).
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
        public void OpenWindow(MainPage page) { }
        public void Quit() { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "screens";
        var print = args.Contains("--print");
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
        window.ShowActivity(feed.Items);
        window.ShowAttention(
        [
            new("Conflict copy: report (conflict LAPTOP 2026-10-02 141509).docx",
                "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { }),
            new("Docs/Report.TXT", "Another file with the same name in different letter case exists on the server.", "Show in folder", () => { }),
        ]);
        const string folder = @"D:\Sync\Work";

        var server = new ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", true, utc, "Windows 1.0.58"),
            new("LAPTOP", true, utc, "Windows 1.0.58"),
        ];
        window.ShowUpdate("Tether 1.0.58 is available", "You have 1.0.52. It takes about 10 seconds and Tether restarts by itself.", "Update now");
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
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-main-syncing-{suffix}.png", print);

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
            Devices = [new("DESKTOP", true, utc), new("LAPTOP", false, utc.AddHours(-3), "Windows 1.0.58")],
        }, folder, "DESKTOP");
        window.Navigate(MainPage.Activity);
        Save(window, outDir, $"windows-main-activity-{suffix}.png", print);

        window.Navigate(MainPage.History);
        window.LoadHistoryAsync().GetAwaiter().GetResult();
        window.SelectHistoryRow(0);
        Save(window, outDir, $"windows-main-history-{suffix}.png", print);

        window.ShowSettingsPage(new SettingsView(new ClientSettings { ServerUrl = "http://100.x.y.z:5075/", Folder = folder, DeviceName = "DESKTOP" },
            new NoProtector(), firstRun: false, serverVersionText: "Server 1.0.58"));
        Save(window, outDir, $"windows-main-settings-{suffix}.png", print);
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
        panel.Close();

        var settings = new SettingsWindow(new ClientSettings { DeviceName = "DESKTOP", UploadLimitMBps = 5 }, new NoProtector(), firstRun: true) { Height = 1320 };
        Place(settings);
        settings.ShowTestResult(true, "Connected. Server and token are OK.");
        Save(settings, outDir, $"windows-settings-first-run-{suffix}.png", print);
        settings.Close();

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

    private static void Place(Window window)
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

    private static void Save(Window window, string outDir, string name, bool print)
    {
        Settle(window);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        const double scale = 1.5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
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
