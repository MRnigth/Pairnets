using System.Windows.Controls;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Tests;

/// <summary>
/// The left side shows where you are: this computer's row while the Overview is open, the nest's row while Computers is
/// open, soft blue; a row with a problem keeps its warm colour and gets a blue ring instead. Same as the Mac/Linux app.
/// </summary>
public sealed class SideCurrentPageTests
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static StatusSnapshot Idle()
    {
        var utc = DateTimeOffset.UtcNow;
        return StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1),
            Server = new ServerInfo("id", 1, 1, "1.0.61", 412L << 30, 1L << 40),
            Devices =
            [
                new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.61", "Windows", utc.AddMinutes(-5), "d1", "desktop"),
                new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.61", "Windows", utc.AddMinutes(-9), "d2", "laptop"),
            ],
        };
    }

    private static bool Filled(MainWindow window, Button row, string key) => Equals(row.Background, window.FindResource(key));

    private static bool Ringed(Button row) => row.BorderThickness.Left >= 2; // the dock buttons always have a thin edge

    [Fact]
    public async Task TheRowOfTheOpenPageIsSoftBlueAndAProblemRowGetsARing()
    {
        await Wpf.Ui(() =>
        {
            var window = new MainWindow(new RecordingActions()) { ThisDevice = "DESKTOP", Width = 1200, Height = 860 };
            window.Left = -20000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            try
            {
                window.ShowStatus(Idle(), @"D:\Sync\Work", "DESKTOP");
                var thisCard = (Button)window.FindName("ThisCard");
                var nestRow = (Button)window.FindName("NestRow");
                var dockThis = (Button)window.FindName("DockThis");
                var dockNest = (Button)window.FindName("DockNest");

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
                window.ShowStatus(Idle() with { Status = RunnerStatus.Syncing, Text = "Syncing", BytesPerSecond = 1 << 20, UpBytesPerSecond = 1 << 20 },
                    @"D:\Sync\Work", "DESKTOP");
                Assert.False(Filled(window, thisCard, "T.AccentSoft"));

                window.ShowStatus(Idle() with { Status = RunnerStatus.Blocked, Text = "The synced folder is missing", BlockReason = BlockReason.FolderMissing },
                    @"D:\Sync\Work", "DESKTOP");
                window.Navigate(MainPage.Overview);
                Assert.True(Filled(window, thisCard, "T.WarnPill"));
                Assert.True(Ringed(thisCard));
                window.Navigate(MainPage.Activity);
                Assert.True(Filled(window, thisCard, "T.WarnPill"));
                Assert.False(Ringed(thisCard));
            }
            finally
            {
                window.AllowClose = true;
                window.Close();
            }
        });
    }
}
