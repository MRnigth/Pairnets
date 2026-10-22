using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The left side shows where you are: this computer's row while the Overview is open, the nest's row while Computers is
/// open, soft blue; a row with a problem keeps its warm colour and gets a blue ring instead.
/// </summary>
public class SideCurrentPageTests
{
    private static readonly DateTimeOffset Utc = DateTimeOffset.UtcNow;

    private static StatusSnapshot Idle() => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = Utc.AddMinutes(-1),
        Server = new ServerInfo("id", 1, 1, "1.0.61", 412L << 30, 1L << 40),
        Devices =
        [
            new("MacBook", Utc.AddDays(-30), Utc, true, "1.0.61", "macOS", Utc.AddMinutes(-5), "d1", "mini"),
            new("LAPTOP", Utc.AddDays(-30), Utc, true, "1.0.61", "Windows", Utc.AddMinutes(-9), "d2", "laptop"),
        ],
    };

    private static IBrush? Resource(MainWindow window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) ? value as IBrush : null;

    private static bool Filled(MainWindow window, Button row, string key) => Equals(row.Background, Resource(window, key));

    private static bool Ringed(Button row) => row.BorderThickness.Left >= 2; // the dock buttons always have a thin edge

    [AvaloniaFact]
    public void TheRowOfTheOpenPageIsSoftBlueAndAProblemRowGetsARing()
    {
        var window = new MainWindow(new SampleActions()) { ThisDevice = "MacBook", Width = 1200, Height = 860 };
        window.Show();
        window.ShowStatus(Idle(), null);
        var thisCard = window.FindControl<Button>("ThisCard")!;
        var nestRow = window.FindControl<Button>("NestRow")!;
        var dockThis = window.FindControl<Button>("DockThis")!;
        var dockNest = window.FindControl<Button>("DockNest")!;

        window.Navigate(MainPage.Overview);
        Assert.True(Filled(window, thisCard, "T.AccentSoft"));
        Assert.False(Filled(window, nestRow, "T.AccentSoft"));
        Assert.True(Filled(window, dockThis, "T.AccentSoft"));
        Assert.False(Filled(window, dockNest, "T.AccentSoft"));

        window.Navigate(MainPage.Devices);
        Assert.False(Filled(window, thisCard, "T.AccentSoft"));
        Assert.True(Filled(window, nestRow, "T.AccentSoft"));
        Assert.True(Filled(window, dockNest, "T.AccentSoft"));
        Assert.True(Ringed(dockNest));
        Assert.False(Ringed(dockThis));

        window.Navigate(MainPage.Account);
        Assert.False(Filled(window, thisCard, "T.AccentSoft"));
        Assert.False(Filled(window, nestRow, "T.AccentSoft"));

        // Syncing no longer paints the row blue on another page: blue means "this page is open"
        window.ShowStatus(Idle() with { Status = RunnerStatus.Syncing, Text = "Syncing", BytesPerSecond = 1 << 20, UpBytesPerSecond = 1 << 20 }, null);
        Assert.False(Filled(window, thisCard, "T.AccentSoft"));

        window.ShowStatus(Idle() with { Status = RunnerStatus.Blocked, Text = "The synced folder is missing", BlockReason = BlockReason.FolderMissing }, null);
        window.Navigate(MainPage.Overview);
        Assert.True(Filled(window, thisCard, "T.WarnPill"));
        Assert.True(Ringed(thisCard));
        window.Navigate(MainPage.Activity);
        Assert.True(Filled(window, thisCard, "T.WarnPill"));
        Assert.False(Ringed(thisCard));
        window.Close();
    }

    /// <summary>The pictures for the review, when PAIRNETS_SCREENSHOT_DIR is set.</summary>
    [AvaloniaFact]
    public void RenderSideCurrentScreenshots()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var window = new MainWindow(new SampleActions()) { ThisDevice = "MacBook", Width = 1200, Height = 860 };
            window.Show();
            window.ShowStatus(Idle(), null);
            window.Navigate(MainPage.Overview);
            DesktopUiTests.Save(window, outDir, $"mac-sidebar-1-overview-{suffix}.png");
            window.Navigate(MainPage.Devices);
            DesktopUiTests.Save(window, outDir, $"mac-sidebar-2-computers-{suffix}.png");
            window.ShowStatus(Idle() with { Status = RunnerStatus.Blocked, Text = "The synced folder is missing", BlockReason = BlockReason.FolderMissing }, null);
            window.Navigate(MainPage.Overview);
            DesktopUiTests.Save(window, outDir, $"mac-sidebar-3-warn-overview-{suffix}.png");
            window.Close();
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }
}
