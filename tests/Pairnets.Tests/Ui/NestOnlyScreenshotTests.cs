using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// Pictures of "Where your files live" after the nest-only fix (2026-10-22), as the Windows tool draws them: the card (the
/// whole-folder row without "Only on the nest"), the questions naming what a choice changes, the line under the card while
/// files come back, and the Overview's nest-only folders. Written when PAIRNETS_SCREENSHOT_DIR is set.
/// </summary>
[Collection(AvaloniaUi.Name)]
public class NestOnlyScreenshotTests
{
    private static readonly List<Window> Opened = [];

    static NestOnlyScreenshotTests() => Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) =>
    {
        lock (Opened)
        {
            if (sender is Window w)
                Opened.Add(w);
        }
    });

    private static Task<Window> PopupAsync(Window owner, string title) => Wait.For(() =>
    {
        lock (Opened)
            return Opened.FirstOrDefault(w => w != owner && w.IsVisible && w.Title == title);
    }, $"the pop-up \"{title}\"");

    [AvaloniaFact]
    public async Task RenderNestOnlyScreens()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            await RenderAsync(outDir, suffix);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static ClientSettings Settings() => new()
    {
        ServerUrl = "https://sync.example.com/n/nst_example/", Folder = "/home/me/Pairnets", DeviceName = "laptop", DeviceId = "id", AccountEmail = "alex@example.com",
    };

    private static async Task RenderAsync(string? outDir, string suffix)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40);
        var nest = FakeNestFiles.ScreenshotNest();
        var window = new MainWindow(new SampleActions()) { Width = 1200, Height = 860 };
        window.Show();
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = [] };
        window.ShowStatus(idle, "/home/me/Pairnets", "laptop");

        // 1. The card: the whole-folder row offers no "Only on the nest"; DCIM offers all its choices.
        var before = new SettingsView(Settings(), new MemorySecrets(), autoStart: false, files: nest);
        window.ShowSettingsPage(before);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        before.ScrollToPlacement();
        DesktopUiTests.Save(window, outDir, $"desktop-nest-only-card-before-{suffix}.png");

        // 2. The questions name what the choice changes, loose files first.
        await QuestionAsync(window, outDir, $"desktop-nest-only-question-whole-folder-{suffix}.png", before.ChoosePlacementAsync("", FilePlacement.OnDemand),
            "Make all your files On-Demand?");
        await QuestionAsync(window, outDir, $"desktop-nest-only-question-dcim-{suffix}.png", before.ChoosePlacementAsync("DCIM", FilePlacement.NestOnly),
            "Keep DCIM only on your nest?");

        // 3. After a choice: what was saved, and the download coming back (or why it waits).
        var nestOnly = Settings();
        nestOnly.Placement["DCIM"] = FilePlacement.NestOnly;
        nest.Placement["DCIM"] = FilePlacement.NestOnly;
        var after = new SettingsView(nestOnly, new MemorySecrets(), autoStart: false, files: nest);
        window.ShowSettingsPage(after);
        await after.ChoosePlacementAsync("DCIM", FilePlacement.OnThisPc); // back to this computer asks nothing
        after.ShowPlacementStatus(idle with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", CurrentPath = "DCIM/104_PANA/P1040298.JPG", Operation = "download",
            FilesTotal = 141, FilesDone = 37, PassBytesTotal = 141 * 3_400_000L, PassBytesDone = 37 * 3_400_000L, BytesPerSecond = 9_800_000, MovingDown = true,
            Active = [new ActiveTransfer("DCIM/104_PANA/P1040298.JPG", "download", 1_200_000, 3_400_000)],
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        after.ScrollToPlacement();
        DesktopUiTests.Save(window, outDir, $"desktop-nest-only-card-after-downloading-{suffix}.png");
        after.ShowPlacementStatus(idle with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.FolderEmpty, PendingDeletes = 1, Text = "Deletions blocked" });
        DesktopUiTests.Save(window, outDir, $"desktop-nest-only-card-after-paused-{suffix}.png");

        // 4. The Overview lists the folders kept on the nest only.
        window.ShowStatus(idle with { NestOnlyFolders = ["DCIM"] }, "/home/me/Pairnets", "laptop");
        window.Navigate(MainPage.Overview);
        DesktopUiTests.Save(window, outDir, $"desktop-nest-only-overview-{suffix}.png");
        window.Close();
    }

    private static async Task QuestionAsync(Window window, string? outDir, string name, Task choosing, string title)
    {
        var question = await PopupAsync(window, title);
        DesktopUiTests.Save(question, outDir, name);
        Person.Click(Screen.Find<Button>(question, "Cancel"));
        await choosing;
    }
}
