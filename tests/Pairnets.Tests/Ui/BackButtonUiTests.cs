using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>The back and forward buttons above every page of the Mac/Linux app (the same as the Windows app).</summary>
public class BackButtonUiTests
{
    [AvaloniaFact]
    public void BackAndForwardRetraceThePages()
    {
        var window = new MainWindow(new SampleActions());
        window.Show();
        Assert.False(window.BackButton.IsEnabled); // nowhere to go back to yet
        Assert.False(window.ForwardButton.IsEnabled);

        window.ClickNav(MainPage.Activity);
        window.Navigate(MainPage.Files);
        window.Navigate(MainPage.History); // the Deleted tab: the same page, no step
        Assert.True(window.BackButton.IsEnabled);
        Assert.Equal("Back to Activity (" + Shortcuts.Hint(ShortcutAction.Back, OperatingSystem.IsMacOS()) + ")", ToolTip.GetTip(window.BackButton));

        window.BackButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(MainPage.Activity, window.Page);
        Assert.True(window.NavActivity.IsChecked); // the tile follows
        Assert.True(window.Run(ShortcutAction.Back));
        Assert.Equal(MainPage.Overview, window.Page);
        Assert.False(window.BackButton.IsEnabled);
        Assert.True(window.ForwardButton.IsEnabled);
        Assert.Equal("Forward to Activity (" + Shortcuts.Hint(ShortcutAction.Forward, OperatingSystem.IsMacOS()) + ")", ToolTip.GetTip(window.ForwardButton));

        window.GoForward();
        window.GoForward();
        Assert.Equal(MainPage.History, window.Page); // back on the tab that was open
        Assert.False(window.ForwardButton.IsEnabled);

        window.GoBack();
        window.ClickNav(MainPage.Devices); // a new path: forward is gone
        Assert.False(window.ForwardButton.IsEnabled);
        window.GoBack();
        Assert.Equal(MainPage.Activity, window.Page);
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void BackToSettingsAsksForAFreshForm()
    {
        var actions = new SampleActions();
        var window = new MainWindow(actions);
        window.Show();
        window.ClickNav(MainPage.Settings);
        window.ShowSettingsPage(new SettingsView(new ClientSettings { ServerUrl = "http://example.invalid:5075/", Folder = "/tmp/x", DeviceName = "mac" }, null, autoStart: false));
        window.ClickNav(MainPage.Activity);
        Assert.False(window.SettingsShown);
        window.GoBack();
        Assert.Equal(["settings", "settings"], actions.Calls); // the controller hands over a fresh form, as from the tile
        window.AllowClose = true;
        window.Close();
    }
}
