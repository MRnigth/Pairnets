using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Pairnets.Client.Ui;
using Pairnets.Core.Client;

namespace Pairnets.Client.Tests;

/// <summary>The back and forward buttons above every page of the Windows app (the same as the Mac/Linux app).</summary>
public sealed class BackButtonTests
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static async Task<(MainWindow Window, RecordingActions Actions)> OpenAsync()
    {
        var actions = new RecordingActions();
        var window = await Wpf.Ui(() =>
        {
            var w = new MainWindow(actions) { Width = 1100, Height = 760 };
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
    public async Task BackAndForwardRetraceThePages()
    {
        var (window, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            Assert.False(window.BackButton.IsEnabled); // nowhere to go back to yet
            Assert.False(window.ForwardButton.IsEnabled);

            window.NavActivity.IsChecked = true; // a tile, as a person would press it
            window.Navigate(MainPage.Files);
            window.Navigate(MainPage.History); // the Deleted tab: the same page, no step
            Assert.True(window.BackButton.IsEnabled);
            Assert.Equal("Back to Activity (Alt+Left)", window.BackButton.ToolTip);

            window.BackButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(MainPage.Activity, window.Page);
            Assert.True(window.NavActivity.IsChecked); // the tile follows
            Assert.True(window.Run(ShortcutAction.Back));
            Assert.Equal(MainPage.Overview, window.Page);
            Assert.False(window.BackButton.IsEnabled);
            Assert.True(window.ForwardButton.IsEnabled);
            Assert.Equal("Forward to Activity (Alt+Right)", window.ForwardButton.ToolTip);

            window.GoForward();
            window.GoForward();
            Assert.Equal(MainPage.History, window.Page); // back on the tab that was open
            Assert.True(window.DeletedFilesTab.IsChecked);
            Assert.False(window.ForwardButton.IsEnabled);

            window.GoBack();
            window.NavDevices.IsChecked = true; // a new path: forward is gone
            Assert.False(window.ForwardButton.IsEnabled);
            window.GoBack();
            Assert.Equal(MainPage.Activity, window.Page);
        });
        await Done();
    }

    [Fact]
    public async Task BackToSettingsAsksForAFreshForm()
    {
        var (window, actions) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.NavSettings.IsChecked = true;
            window.ShowSettingsPage(new Border());
            window.NavActivity.IsChecked = true;
            window.GoBack();
        });
        Assert.Equal([nameof(ITrayActions.ShowSettings), nameof(ITrayActions.ShowSettings)], actions.Calls.Items.Where(c => c == nameof(ITrayActions.ShowSettings)));
        await Done();
    }
}
