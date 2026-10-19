using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// Pictures of the pages of the "new look" stage 4 (Activity, Needs you, History, Devices, Settings, Account), with sample
/// data only. They are named stage4-&lt;page&gt;-&lt;light|dark&gt;.png and written when PAIRNETS_SCREENSHOT_DIR is set.
/// </summary>
internal static class Stage4Screens
{
    private const string Folder = "/home/me/Work";

    public static void Render(string? outDir, string suffix)
    {
        var now = DateTimeOffset.Now;
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, "1.0.48", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-20), utc, true, "1.0.48", "Windows") { LastChange = utc.AddMinutes(-20) },
            new("LAPTOP", utc.AddDays(-20), utc, true, "1.0.48", "Windows") { LastChange = utc.AddMinutes(-35) },
            new("MINI-PC", utc.AddDays(-18), utc, true, "1.0.48", "Linux") { LastChange = utc.AddHours(-3) },
            new("MEDIA-PC", utc.AddDays(-15), utc.AddDays(-1), false, "1.0.47", "Linux") { LastChange = utc.AddDays(-1) },
            new("OFFICE-PC", utc.AddDays(-12), utc.AddDays(-2), false, "1.0.47", "Windows") { LastChange = utc.AddDays(-2) },
            new("KITCHEN-PC", utc.AddDays(-10), utc.AddDays(-3), false, "1.0.47", "Windows") { LastChange = utc.AddDays(-3) },
        ];
        var status = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now.AddMinutes(-1), Server = server, Devices = devices,
            NestUrl = "https://nest.example.com",
            JoinRequests = [new JoinRequest("RX4K-92TB", "PC-1", "Windows", utc.AddMinutes(-4))],
            Account = new AccountInfo("alex@example.com", "google", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)),
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(0, 4.1 * (1 << 20), 0, 0, 0, 0, Downloading: true), utc, 80, utc.AddMinutes(-2)),
            },
        };
        var feed = Activity(now);
        var actions = new SampleActions();

        var window = new MainWindow(actions) { Width = 1200, Height = 780 };
        window.Show();
        window.ShowStatus(status, Folder, "DESKTOP");
        window.ShowActivity(feed);
        window.ShowAttention(NeedsItems(utc));

        // Activity: the full list with a folder row opened
        window.Navigate(MainPage.Activity);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        foreach (var chevron in Screen.All<ToggleButton>(window).Where(t => t.Name == "FolderChevron").Take(1))
            chevron.IsChecked = true;
        Save(window, outDir, "activity", suffix);
        window.SearchActivity("zzz");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Save(window, outDir, "activity-search-empty", suffix);
        window.SearchActivity(string.Empty);

        // Needs you
        window.Navigate(MainPage.Attention);
        Save(window, outDir, "needs", suffix);

        // History
        window.Navigate(MainPage.History);
        window.LoadHistoryAsync().GetAwaiter().GetResult();
        Save(window, outDir, "history-deleted", suffix);
        window.ShowAllFiles();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.SelectHistoryRow(0);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Save(window, outDir, "history-all", suffix);

        // Devices
        window.Navigate(MainPage.Devices);
        Save(window, outDir, "devices", suffix);

        // Settings, and scrolled to "Start over"
        var settings = new SettingsView(new ClientSettings
        {
            ServerUrl = "https://nest.example.com/", Folder = Folder, DeviceName = "DESKTOP", DeviceId = "id", AccountEmail = "alex@example.com",
            ExtraIgnore = ["*.bak", "node_modules/", "build/"],
        }, null, autoStart: true, serverVersionText: "Nest 1.0.48");
        window.ShowSettingsPage(settings);
        Save(window, outDir, "settings", suffix);
        settings.ScrollToEnd();
        Save(window, outDir, "settings-start-over", suffix);

        // Account, and the same page for an older nest that sends no email
        window.Navigate(MainPage.Account);
        Save(window, outDir, "account", suffix);
        window.ShowStatus(status with { Account = null }, Folder, "DESKTOP");
        Save(window, outDir, "account-old-nest", suffix);
        window.AllowClose = true;
        window.Close();

        // Activity, nothing synced yet
        var empty = new MainWindow(actions) { Width = 1200, Height = 780 };
        empty.Show();
        empty.ShowStatus(status, Folder, "DESKTOP");
        empty.ShowActivity([]);
        empty.Navigate(MainPage.Activity);
        Save(empty, outDir, "activity-empty", suffix);
        empty.AllowClose = true;
        empty.Close();
    }

    private static IReadOnlyList<ActivityItem> Activity(DateTimeOffset now)
    {
        var feed = new ActivityFeed(300);
        var yesterday = now.AddDays(-1);
        feed.Add(new ActivityItem(yesterday.AddHours(-3).AddMinutes(-2), ActivityKind.Downloaded, "Reports/q3-summary.pdf", "Downloaded"));
        for (var i = 0; i < 12; i++)
            feed.Add(new ActivityItem(yesterday.AddHours(-2).AddSeconds(i * 8), ActivityKind.Downloaded, $"Reports/chart-{i + 1:00}.xlsx", "Downloaded"));
        feed.Add(new ActivityItem(yesterday.AddHours(-1), ActivityKind.Uploaded, "Talks/presentation-final.mp4", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-190), ActivityKind.DeletedHere, "Drafts/old-draft.docx", "Deleted"));
        feed.Add(new ActivityItem(now.AddMinutes(-170), ActivityKind.Uploaded, "Notes/meeting-2026-10-02.md", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-150), ActivityKind.Conflict, "Finance/budget (conflict LAPTOP 2026-10-02 141509).xlsx", "Conflict"));
        feed.Add(new ActivityItem(now.AddMinutes(-120), ActivityKind.Downloaded, "Photos/Holiday 2026/holiday-0412.jpg", "Downloaded"));
        for (var i = 0; i < 37; i++)
            feed.Add(new ActivityItem(now.AddMinutes(-90).AddSeconds(i * 3), ActivityKind.Uploaded, $"Photos/Holiday 2026/holiday-{414 + i:0000}.jpg", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-30), ActivityKind.Uploaded, "Reports/report-q3.xlsx", "Uploaded"));
        return feed.Items;
    }

    private static List<AttentionItem> NeedsItems(DateTimeOffset utc) =>
    [
        new("PC-1 (Windows) wants to join", "Code RX4K-92TB. Check that it matches the code on that computer, then allow or deny it on your nest.",
            "Review in browser", () => { }, AttentionKind.Join, "RX4K-92TB", utc.AddMinutes(-4)),
        new("Conflict copy: budget (conflict LAPTOP 2026-10-02 141509).xlsx", "Both computers changed this file. Compare the two, keep what you want, delete the other.",
            "Show in folder", () => { }, AttentionKind.Conflict, null, utc.AddMinutes(-150)),
        new("Report.pdf and report.pdf can't share a folder", "The names differ only in capital letters, which Windows sees as the same name. report.pdf waits on your nest until one of them has a new name.",
            "Show in folder", () => { }, AttentionKind.NameClash),
    ];

    private static void Save(Window window, string? outDir, string page, string suffix) =>
        DesktopUiTests.Save(window, outDir, $"stage4-{page}-{suffix}.png");
}
