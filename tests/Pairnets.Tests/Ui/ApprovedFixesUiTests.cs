using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The approved design fixes of the first batch, in the Mac/Linux app: the header's Sync now follows the state (A1), the
/// keyboard shortcuts and the Mac menu bar (A4), the page tiles under the computers (A12) and the failed update's reason (A13).
/// </summary>
public class ApprovedFixesUiTests
{
    private static readonly ServerInfo Server = new("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40);

    private static StatusSnapshot Status(RunnerStatus status) =>
        StatusSnapshot.Initial with { Status = status, Text = status.ToString(), LastSyncAt = DateTimeOffset.UtcNow.AddMinutes(-5), Server = Server, Devices = [] };

    [AvaloniaFact]
    public void SyncNowIsTheBlueButtonOnlyWhenItIsTheUsefulAction()
    {
        var window = new MainWindow(new SampleActions()) { ThisDevice = "MacBook" };
        window.Show();

        window.ShowStatus(Status(RunnerStatus.Idle), "/Users/me/Sync");
        Assert.True(window.SyncNowIsPrimary);
        Assert.True(window.SyncNowEnabled);

        window.ShowStatus(Status(RunnerStatus.Syncing) with { FilesTotal = 3, Operation = "upload", CurrentPath = "a.txt", MovingUp = true }, null);
        Assert.False(window.SyncNowIsPrimary);
        Assert.False(window.SyncNowEnabled); // already syncing

        window.ShowStatus(Status(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignedOut }, null);
        Assert.False(window.SyncNowIsPrimary); // "Sign in again" on the card is the blue one
        Assert.True(window.SyncNowEnabled);
    }

    [AvaloniaFact]
    public void ShortcutsOpenThePagesAndSync()
    {
        var actions = new SampleActions();
        var window = new MainWindow(actions) { ThisDevice = "MacBook" };
        window.Show();
        window.ShowStatus(Status(RunnerStatus.Idle), "/Users/me/Sync");

        Press(window, Key.D4);
        Assert.Equal(MainPage.History, window.Page);
        Press(window, Key.D2);
        Assert.Equal(MainPage.Activity, window.Page);
        Press(window, Key.D1);
        Assert.Equal(MainPage.Overview, window.Page);
        Press(window, Key.R);
        Press(window, Key.F5, KeyModifiers.None);
        Assert.Equal(["sync", "sync"], actions.Calls);
        Press(window, Key.OemComma);
        Assert.Contains("settings", actions.Calls); // the app hands over a fresh form

        // Nothing to search on the Overview: Ctrl+F does nothing there.
        Assert.False(window.Run(ShortcutAction.Search));
    }

    [AvaloniaFact]
    public void TheMacMenuBarHasTheAppMenuAndFiveMenus()
    {
        var window = new MainWindow(new SampleActions());
        var (app, bar) = window.MakeMacMenus();

        var appItems = app.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal(["About Pairnets", "Settings…"], appItems.Select(i => i.Header));
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), appItems[1].Gesture);

        var menus = bar.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal(["File", "Sync", "View", "Window", "Help"], menus.Select(m => m.Header));
        var view = menus[2].Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal(7, view.Count); // six pages and folding the side panel (the line between is left out)
        Assert.Equal(new KeyGesture(Key.D1, KeyModifiers.Meta), view[0].Gesture);
        Assert.Equal("Fold Side Panel", view[6].Header);
        Assert.Equal(new KeyGesture(Key.R, KeyModifiers.Meta), menus[1].Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).First().Gesture);
        Assert.Equal("Report a Bug…", menus[4].Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).Single().Header);
    }

    [AvaloniaFact]
    public void ThePageTilesSitRightUnderTheComputers()
    {
        var window = new MainWindow(new SampleActions()) { ThisDevice = "MacBook", Height = 900 };
        window.Show();
        window.ShowStatus(Status(RunnerStatus.Idle), "/Users/me/Sync");
        window.UpdateLayout();

        var tile = Screen.Named<RadioButton>(window, "NavSettings");
        var bottom = tile.TranslatePoint(new Point(0, tile.Bounds.Height), window)!.Value.Y;
        Assert.True(bottom < window.Bounds.Height - 120, $"the last tile ends at {bottom} of {window.Bounds.Height}: the spare space should be under it");
    }

    [AvaloniaFact]
    public void AFailedUpdateSaysWhyAndOnlyTheXPutsItAway()
    {
        var window = new MainWindow(new SampleActions());
        window.Show();

        window.ShowUpdate("Pairnets 99.0.1 could not be installed", UpdateProblem.NotSigned.Reason, UpdateProblem.NotSigned.ButtonText);
        Assert.Equal(ReleaseSigning.NotSignedMessage, Screen.Named<TextBlock>(window, "UpdateReason").Text);
        Assert.True(Screen.Named<TextBlock>(window, "UpdateReason").IsVisible);
        Assert.False(Screen.Named<Button>(window, "UpdateButton").IsVisible);
        Assert.True(Screen.Named<Button>(window, "UpdateLater").IsVisible);

        window.ShowUpdate("Pairnets 99.0.2 is available", "You have 1.0.0.", "Download");
        Assert.False(Screen.Named<TextBlock>(window, "UpdateReason").IsVisible);
        Assert.True(Screen.Named<Button>(window, "UpdateButton").IsVisible);
    }

    private static void Press(Window window, Key key, KeyModifiers? modifiers = null) =>
        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control),
            Source = window,
        });
}
