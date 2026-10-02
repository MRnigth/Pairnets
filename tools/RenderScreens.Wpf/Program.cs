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
        var window = new MainWindow(null) { Width = 720, Height = 700 };
        Place(window);
        var feed = new ActivityFeed();
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
        window.ShowUpdate("Tether 1.0.58 is available", "You have 1.0.52. The update takes about 10 seconds and Tether restarts by itself.", "Update now");
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1),
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 67_108_864, BytesTotal = 104_857_600,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20, BytesPerSecond = 4.9 * (1 << 20),
            LimitText = "Limited to 5 MB/s", Server = server,
            Active =
            [
                new ActiveTransfer("Photos/summer/holiday-0412.jpg", "upload", 78, 100),
                new ActiveTransfer("Photos/summer/holiday-0413.jpg", "upload", 41, 100),
                new ActiveTransfer("Videos/presentation-final.mp4", "upload", 12, 100),
                new ActiveTransfer("Notes/meeting-2026-10-02.md", "upload", 95, 100),
            ],
        }, folder);
        Save(window, outDir, $"windows-main-syncing-{suffix}.png", print);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, LastSyncAt = now.AddMinutes(-3),
            Text = "This sync would delete 37 files on the server (out of 120). Nothing was deleted.",
        }, folder);
        window.ShowAttentionTab(true);
        Save(window, outDir, $"windows-main-blocked-{suffix}.png", print);

        window.ShowUpdate(null, string.Empty, string.Empty);
        window.ShowAttentionTab(false);
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now, Server = server }, folder);
        Save(window, outDir, $"windows-main-idle-{suffix}.png", print);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-4), Server = server,
            WaitingFor = new PeerWait("DESKTOP", 340, 212),
        }, folder);
        Save(window, outDir, $"windows-main-waiting-{suffix}.png", print);
        window.AllowClose = true;
        window.Close();

        var settings = new SettingsWindow(new ClientSettings { DeviceName = "DESKTOP", UploadLimitMBps = 5 }, new NoProtector(), firstRun: true) { Height = 1320 };
        Place(settings);
        settings.ShowTestResult(true, "Connected. Server and token are OK.");
        Save(settings, outDir, $"windows-settings-first-run-{suffix}.png", print);
        settings.Close();

        var serverUpdate = new ServerUpdateWindow(null, "1.0.52", "1.0.58");
        Place(serverUpdate);
        Save(serverUpdate, outDir, $"windows-server-update-{suffix}.png", print);
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

    private static void Save(Window window, string outDir, string name, bool print)
    {
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
