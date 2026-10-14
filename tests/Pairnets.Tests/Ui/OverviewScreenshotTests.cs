using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The Overview in every state of the new look (Content, Ov-*.dc.html, Many-B), light and dark. Pictures are written when
/// PAIRNETS_SCREENSHOT_DIR is set; otherwise it only checks that each state draws and says what it should.
/// </summary>
public class OverviewScreenshotTests
{
    private const double MB = 1 << 20;
    private const string Folder = "/home/me/Work";

    [AvaloniaFact]
    public void RenderStage3Overview()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            Render(outDir, suffix);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static void Render(string? outDir, string suffix)
    {
        var now = DateTimeOffset.Now;
        var utc = DateTimeOffset.UtcNow;
        var window = new MainWindow(new SampleActions()) { Width = 1200, Height = 780 };
        window.Show();
        var server = new ServerInfo("id", 1, 1, "1.0.48", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("MacBook", utc.AddDays(-30), utc, true, "1.0.48", "macOS"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.48", "Windows"),
            new("MINI-PC", utc.AddDays(-30), utc.AddSeconds(-3), true, "1.0.48", "Linux"),
            new("MEDIA-PC", utc.AddDays(-30), utc.AddHours(-2), false, "1.0.48", "Linux"),
            new("OFFICE-PC", utc.AddDays(-30), utc.AddDays(-3), false, "1.0.48", "Windows"),
            new("KITCHEN-PC", utc.AddDays(-30), utc.AddDays(-4), false, "1.0.48", "Windows"),
        ];
        IReadOnlyList<DeviceInfo> many =
        [
            .. devices,
            new("STUDY-PC", utc.AddDays(-30), utc, true, "1.0.48", "Windows"),
            new("GARAGE-PC", utc.AddDays(-30), utc.AddDays(-9), false, "1.0.48", "Linux"),
            new("ATTIC-PC", utc.AddDays(-30), utc.AddDays(-9), false, "1.0.48", "Linux"),
        ];

        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(now.AddMinutes(-33), ActivityKind.Uploaded, "meeting-2026-10-02.md", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-14), ActivityKind.Downloaded, "holiday-0412.jpg", "Downloaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-1), ActivityKind.Uploaded, "report-q3.xlsx", "Uploaded"));
        window.ShowActivity(feed.Items);
        var attention = new List<AttentionItem>
        {
            new("A computer asks to join", "MEDIA-PC", "Review", () => { }),
            new("report (conflict LAPTOP).docx", "Both computers changed this file.", "Show in folder", () => { }),
            new("Docs/Report.TXT", "Another file with the same name exists.", "Show in folder", () => { }),
        };
        window.ShowAttention(attention);

        var basis = StatusSnapshot.Initial with
        {
            LastSyncAt = now.AddMinutes(-15), Server = server, Devices = devices, NestUrl = "https://nest.example.com",
            JoinRequests = [new JoinRequest("KQ7M-4PXD", "MEDIA-PC", "Windows", utc)],
        };
        var syncing = basis with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", Stage = SyncStage.Transferring, CurrentPath = "Photos/Holiday 2026/holiday-0412.jpg", Operation = "upload",
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20, BytesPerSecond = 4.9 * MB, UpBytesPerSecond = 4.9 * MB,
            MovingUp = true, LimitText = "Limited to 5 MB/s",
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(0, 4.1 * MB, 30, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
                ["MINI-PC"] = new(new TransferReport(0, 2.6 * MB, 12, 120, 0, 0, Downloading: true), DateTimeOffset.UtcNow, 0, null),
            },
            Active =
            [
                new ActiveTransfer("Photos/Holiday 2026/holiday-0412.jpg", "upload", 78, 100),
                new ActiveTransfer("Photos/Holiday 2026/holiday-0413.jpg", "upload", 41, 100),
                new ActiveTransfer("presentation-final.mp4", "upload", 12, 100),
                new ActiveTransfer("meeting-2026-10-02.md", "upload", 95, 100),
            ],
            Batch =
            [
                new BatchFile("Photos/Holiday 2026/holiday-0412.jpg", "upload", BatchFileState.Moving, 78),
                new BatchFile("Photos/Holiday 2026/holiday-0413.jpg", "upload", BatchFileState.Moving, 41),
                .. Enumerable.Range(14, 80).Select(i => new BatchFile($"Photos/Holiday 2026/holiday-{i:0000}.jpg", "upload", BatchFileState.Waiting)),
                new BatchFile("presentation-final.mp4", "upload", BatchFileState.Moving, 12),
                new BatchFile("meeting-2026-10-02.md", "upload", BatchFileState.Moving, 95),
            ],
        };

        window.ShowStatus(syncing, Folder, "MacBook");
        Assert.Equal("Uploading 120 files to your nest", window.HeadlineText);
        Assert.True(window.TransferVisible);
        Assert.True(window.MapHereFlowing);
        Save(window, outDir, $"avalonia-overview-syncing-{suffix}.png");
        window.ShowStatus(syncing with { Devices = many }, Folder, "MacBook");
        Save(window, outDir, $"avalonia-overview-many-{suffix}.png");

        window.ShowUpdate("Pairnets 1.0.58 is downloading", "It restarts by itself when it is ready.", "Update now", 42, busy: true);
        window.ShowStatus(syncing, Folder, "MacBook");
        Save(window, outDir, $"avalonia-overview-update-{suffix}.png");
        window.ShowUpdate(null, string.Empty, string.Empty);

        window.ShowStatus(basis with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-1) }, Folder, "MacBook");
        Assert.Equal("Everything is up to date", window.HeadlineText);
        Assert.Equal("LAPTOP: in sync", window.MapOtherText);
        Save(window, outDir, $"avalonia-overview-up-to-date-{suffix}.png");

        window.ShowStatus(basis with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", WaitingFor = new PeerWait("LAPTOP", 86, 46),
            Peers = new Dictionary<string, PeerActivity> { ["LAPTOP"] = new(new TransferReport(3.8 * MB, 0, 46, 86, 0, 0, Uploading: true), DateTimeOffset.UtcNow, 0, null) },
        }, Folder, "MacBook");
        Assert.Equal("Waiting for LAPTOP", window.HeadlineText);
        Assert.True(window.BadgePulses);
        Save(window, outDir, $"avalonia-overview-waiting-{suffix}.png");

        window.ShowAttention([]);
        window.ShowStatus(basis with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, Text = "This sync would delete 37 files.",
            PendingDeletePaths = ["Drafts/old-draft.docx", "Finance/budget.xlsx", "Notes/meeting-2026-10-02.md", "Photos/holiday-0412.jpg"],
        }, Folder, "MacBook");
        Assert.True(window.FixVisible);
        Save(window, outDir, $"avalonia-overview-stopped-{suffix}.png");

        window.ShowAttention(attention);
        window.ShowStatus(basis with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.SignInRequired, Text = "Sign in to your nest" }, Folder, "MacBook");
        Assert.Equal("Sign in to your nest", window.HeadlineText);
        Save(window, outDir, $"avalonia-overview-sign-in-{suffix}.png");
        window.ShowStatus(basis with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.SignedOut, Text = "Signed out" }, Folder, "MacBook");
        Assert.Equal("Signed out of your nest", window.HeadlineText);
        Save(window, outDir, $"avalonia-overview-signed-out-{suffix}.png");
        window.ShowStatus(basis with { Status = RunnerStatus.Offline, Text = "Offline: timeout", PendingChanges = 12 }, Folder, "MacBook");
        Assert.Equal("Can’t reach your nest", window.HeadlineText);
        Save(window, outDir, $"avalonia-overview-offline-{suffix}.png");
        window.ShowStatus(basis with { Status = RunnerStatus.Paused, Text = "Paused", Paused = true, PendingChanges = 5 }, Folder, "MacBook");
        Assert.Equal("Paused", window.HeadlineText);
        Save(window, outDir, $"avalonia-overview-paused-{suffix}.png");
        window.ShowStatus(basis with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.FolderMissing, Text = "The folder is missing." }, Folder, "MacBook");
        Assert.Equal("Can’t find your sync folder", window.HeadlineText);
        Save(window, outDir, $"avalonia-overview-missing-{suffix}.png");

        window.AllowClose = true;
        window.Close();
    }

    private static void Save(Avalonia.Controls.Window window, string? outDir, string name)
    {
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
}
