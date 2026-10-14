using System.Windows;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

public static partial class Program
{
    /// <summary>The Overview in every state of the new look (Content, Ov-*.dc.html, Many-B), with the design's sample data.</summary>
    private static void RenderStage3(string outDir, string suffix, bool print)
    {
        const double MB = 1 << 20;
        var now = DateTimeOffset.Now;
        var utc = DateTimeOffset.UtcNow;
        const string folder = @"D:\Sync\Work";
        var window = new MainWindow(new SampleActions()) { Width = 1200, Height = 830 };
        Place(window);
        var server = new ServerInfo("id", 1, 1, "1.0.48", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.48", "Windows"),
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
        window.ShowStatus(syncing, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-syncing-{suffix}.png", print);
        window.ShowStatus(syncing with { Devices = many }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-many-{suffix}.png", print);

        window.ShowUpdate("Pairnets 1.0.58 is downloading", "It restarts by itself when it is ready.", "Update now", 42, busy: true);
        window.ShowStatus(syncing, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-update-{suffix}.png", print);
        window.ShowUpdate(null, string.Empty, string.Empty);

        window.ShowStatus(basis with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-1) }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-up-to-date-{suffix}.png", print);

        window.ShowStatus(basis with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", WaitingFor = new PeerWait("LAPTOP", 86, 46),
            Peers = new Dictionary<string, PeerActivity> { ["LAPTOP"] = new(new TransferReport(3.8 * MB, 0, 46, 86, 0, 0, Uploading: true), DateTimeOffset.UtcNow, 0, null) },
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-waiting-{suffix}.png", print);

        window.ShowAttention([]);
        window.ShowStatus(basis with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, Text = "This sync would delete 37 files.",
            PendingDeletePaths = ["Drafts/old-draft.docx", "Finance/budget.xlsx", "Notes/meeting-2026-10-02.md", "Photos/holiday-0412.jpg"],
        }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-stopped-{suffix}.png", print);

        window.ShowAttention(attention);
        window.ShowStatus(basis with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.SignInRequired, Text = "Sign in to your nest" }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-sign-in-{suffix}.png", print);
        window.ShowStatus(basis with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.SignedOut, Text = "Signed out" }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-signed-out-{suffix}.png", print);
        window.ShowStatus(basis with { Status = RunnerStatus.Offline, Text = "Offline: timeout", PendingChanges = 12 }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-offline-{suffix}.png", print);
        window.ShowStatus(basis with { Status = RunnerStatus.Paused, Text = "Paused", Paused = true, PendingChanges = 5 }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-paused-{suffix}.png", print);
        window.ShowStatus(basis with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.FolderMissing, Text = "The folder is missing." }, folder, "DESKTOP");
        Save(window, outDir, $"windows-overview-missing-{suffix}.png", print);

        window.AllowClose = true;
        window.Close();
    }
}
