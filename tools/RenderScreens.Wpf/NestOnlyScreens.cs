using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tools;

/// <summary>
/// "Where your files live" after the nest-only fix (2026-10-22): the card (the whole-folder row without "Only on the nest"),
/// the questions naming what a choice changes, the line under the card while files come back, and the Overview's
/// nest-only folders. The sample nest is the one that went wrong: 932 screenshots right in the folder and a camera folder.
/// </summary>
internal static class NestOnlyScreens
{
    public static void Render(string outDir, string suffix, bool print)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40);
        var nest = FakeNestFiles.ScreenshotNest();
        var window = new MainWindow(new Stage6.Actions()) { Width = 1200, Height = 860 };
        Program.Place(window);
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = [] };
        window.ShowStatus(idle, @"D:\Sync\Pairnets", "DESKTOP");

        // 1. The card: the whole-folder row offers On this PC and On-Demand only; DCIM offers all three.
        var before = new SettingsView(Settings(), new Program.NoProtector(), null, "Nest " + PairnetsInfo.ProductVersion, nest);
        window.ShowSettingsPage(before);
        window.UpdateLayout();
        before.ScrollToPlacement();
        Program.Save(window, outDir, $"nest-only-card-before-{suffix}.png", print);

        // 2. The questions name what the choice changes, loose files first.
        Question(outDir, $"nest-only-question-whole-folder-{suffix}.png", print, nest, "", FilePlacement.OnDemand);
        Question(outDir, $"nest-only-question-dcim-{suffix}.png", print, nest, "DCIM", FilePlacement.NestOnly);

        // 3. After a choice: what was saved, and the download coming back.
        var nestOnly = Settings();
        nestOnly.Placement["DCIM"] = FilePlacement.NestOnly;
        nest.Placement["DCIM"] = FilePlacement.NestOnly;
        var after = new SettingsView(nestOnly, new Program.NoProtector(), null, "Nest " + PairnetsInfo.ProductVersion, nest);
        window.ShowSettingsPage(after);
        after.ChoosePlacement("DCIM", FilePlacement.OnThisPc); // back to this PC asks nothing
        after.ShowPlacementStatus(idle with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", CurrentPath = "DCIM/104_PANA/P1040298.JPG", Operation = "download",
            FilesTotal = 141, FilesDone = 37, PassBytesTotal = 141 * 3_400_000L, PassBytesDone = 37 * 3_400_000L, BytesPerSecond = 9_800_000, MovingDown = true,
            Active = [new ActiveTransfer("DCIM/104_PANA/P1040298.JPG", "download", 1_200_000, 3_400_000)],
        });
        window.UpdateLayout();
        after.ScrollToPlacement();
        Program.Save(window, outDir, $"nest-only-card-after-downloading-{suffix}.png", print);

        // The same while the sync waits for a decision (the folder-empty guard): nothing moves until then.
        after.ShowPlacementStatus(idle with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.FolderEmpty, PendingDeletes = 1, Text = "Deletions blocked" });
        Program.Save(window, outDir, $"nest-only-card-after-paused-{suffix}.png", print);

        // 4. The Overview lists the folders kept on the nest only.
        window.ShowStatus(idle with { NestOnlyFolders = ["DCIM"] }, @"D:\Sync\Pairnets", "DESKTOP");
        window.Navigate(MainPage.Overview);
        Program.Save(window, outDir, $"nest-only-overview-{suffix}.png", print);
        window.AllowClose = true;
        window.Close();
    }

    private static ClientSettings Settings() => new()
    {
        ServerUrl = "https://sync.example.com/n/nst_example/", Folder = @"D:\Sync\Pairnets", DeviceName = "DESKTOP", DeviceId = "id",
        AccountEmail = "alex@example.com",
    };

    private static void Question(string outDir, string name, bool print, FakeNestFiles nest, string folder, FilePlacement to)
    {
        var now = nest.PlacementOf(folder);
        var q = FilesPageText.AwayQuestion(folder, to, nest.BytesHere(folder), FilesPageText.Affects(nest.List(folder), folder, now));
        var popup = Dialogs.ChoicePreview(q.Title, q.Text, q.Points, "Cancel", q.Button);
        // Drawn from a frame without margins of its own (the capture draws the content's layout box).
        var body = (System.Windows.UIElement)popup.Content;
        popup.Content = null;
        popup.Content = new System.Windows.Controls.Border { Child = body, Width = popup.Width };
        Program.Place(popup);
        Program.Save(popup, outDir, name, print);
        popup.Close();
    }
}
