using System.Windows;
using System.Windows.Controls;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Tests;

/// <summary>
/// The approved design fixes of the first batch, in the Windows app: the header's Sync now follows the state (A1), the
/// keyboard shortcuts (A4), the page tiles under the computers (A12) and the failed update's reason (A13).
/// </summary>
public sealed class ApprovedFixesTests
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static readonly ServerInfo Server = new("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40);

    private static StatusSnapshot Status(RunnerStatus status) =>
        StatusSnapshot.Initial with { Status = status, Text = status.ToString(), LastSyncAt = DateTimeOffset.UtcNow.AddMinutes(-5), Server = Server, Devices = [] };

    private static async Task<(MainWindow Window, RecordingActions Actions)> OpenAsync()
    {
        var actions = new RecordingActions(new SampleHistory());
        var window = await Wpf.Ui(() =>
        {
            var w = new MainWindow(actions) { Width = 1180, Height = 900 };
            w.Show();
            return w;
        });
        await Wpf.WaitUntil(() => window.IsLoaded, "the window to open");
        return (window, actions);
    }

    private static async Task Done()
    {
        await Wpf.Settle();
        Assert.Empty(Wpf.TakeCrashes());
        await Wpf.CloseAllWindows();
    }

    [Fact]
    public async Task SyncNowIsTheBlueButtonOnlyWhenItIsTheUsefulAction()
    {
        var (window, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowStatus(Status(RunnerStatus.Idle), @"D:\Sync");
            Assert.True(window.SyncNowIsPrimary);
            Assert.True(window.SyncNowButton.IsEnabled);

            window.ShowStatus(Status(RunnerStatus.Syncing) with { FilesTotal = 3, Operation = "upload", CurrentPath = "a.txt", MovingUp = true }, null);
            Assert.False(window.SyncNowIsPrimary);
            Assert.False(window.SyncNowButton.IsEnabled); // already syncing
            Assert.Equal("Pairnets is syncing now", window.SyncNowButton.ToolTip);

            window.ShowStatus(Status(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignedOut }, null);
            Assert.False(window.SyncNowIsPrimary); // "Sign in again" on the card is the blue one
            Assert.True(window.SyncNowButton.IsEnabled);
            Assert.Equal("Sync now (Ctrl+R)", window.SyncNowButton.ToolTip);
            Assert.NotEqual(System.Windows.Media.Colors.White, ((System.Windows.Media.SolidColorBrush)window.SyncNowIcon.Stroke!).Color); // the plain button's icon shows
        });
        await Done();
    }

    [Fact]
    public async Task ShortcutsOpenThePagesAndSync()
    {
        var (window, actions) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowStatus(Status(RunnerStatus.Idle), @"D:\Sync");
            window.ShowActivity([new ActivityItem(DateTimeOffset.Now.AddMinutes(-3), ActivityKind.Uploaded, "Notes/a.md", "Uploaded")]); // the search box shows once something synced
            Assert.True(window.Run(ShortcutAction.PageFiles));
            Assert.Equal(MainPage.Files, window.Page);
            Assert.True(window.Run(ShortcutAction.PageActivity));
            Assert.Equal(MainPage.Activity, window.Page);
            Assert.True(window.Run(ShortcutAction.Search)); // Activity has a search box
            Assert.True(window.Run(ShortcutAction.PageOverview));
            Assert.False(window.Run(ShortcutAction.Search)); // nothing to search on the Overview
            Assert.True(window.Run(ShortcutAction.SyncNow));
            Assert.True(window.Run(ShortcutAction.Settings));
            Assert.Equal("Overview (Ctrl+1)", window.DockOverview.ToolTip);
        });
        Assert.Contains("SyncNow", actions.Calls.Items);
        Assert.Contains("ShowSettings", actions.Calls.Items);
        await Done();
    }

    [Fact]
    public async Task ThePageTilesSitRightUnderTheComputers()
    {
        var (window, _) = await OpenAsync();
        var (bottom, height) = await Wpf.Ui(() =>
        {
            window.ShowStatus(Status(RunnerStatus.Idle), @"D:\Sync");
            window.UpdateLayout();
            var tile = window.NavSettings;
            return (tile.TranslatePoint(new Point(0, tile.ActualHeight), window).Y, window.ActualHeight);
        });
        Assert.True(bottom < height - 120, $"the last tile ends at {bottom} of {height}: the spare space should be under it");
        await Done();
    }

    [Fact]
    public async Task AFailedUpdateSaysWhyAndOnlyTheXPutsItAway()
    {
        var (window, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowUpdate("Pairnets 99.0.1 could not be installed", UpdateProblem.NotSigned.Reason, UpdateProblem.NotSigned.ButtonText);
            Assert.Equal(ReleaseSigning.NotSignedMessage, window.UpdateReason.Text);
            Assert.Equal(Visibility.Visible, window.UpdateReason.Visibility);
            Assert.Equal(Visibility.Collapsed, window.UpdateButton.Visibility);
            Assert.Equal(Visibility.Visible, window.UpdateLater.Visibility);

            var tryAgain = UpdateProblem.From(new HttpRequestException("reset"));
            window.ShowUpdate("Pairnets 99.0.1 could not be installed", tryAgain.Reason, tryAgain.ButtonText);
            Assert.Equal("Try again", window.UpdateButton.Content);
            Assert.Equal(Visibility.Visible, window.UpdateButton.Visibility);
        });
        await Done();
    }
}
