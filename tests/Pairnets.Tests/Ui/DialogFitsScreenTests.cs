using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pairnets.Core.Client;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// On a Mac the "allow deletions?" window listed 3340 files as one long text, grew taller than the screen, and its
/// Yes/Cancel buttons could not be reached. A question must fit the screen: the text scrolls, the buttons stay in view.
/// </summary>
[Collection(AvaloniaUi.Name)]
public class DialogFitsScreenTests
{
    private const double SmallScreen = 600;

    private static string HugeList()
    {
        var sb = new System.Text.StringBuilder("Pairnets stopped because this sync would delete 3340 file(s):\n\n");
        for (var i = 0; i < 3340; i++)
            sb.AppendLine($"  server: Photos/2025/Holiday in the mountains/very long folder name number {i}/IMG_{i:0000}_with_a_really_long_file_name_that_goes_on.jpg");
        sb.Append("\nDelete these files once?");
        return sb.ToString();
    }

    private static void Layout()
    {
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Opens a dialog on a pretend small screen; returns it and puts the old screen size back when disposed.</summary>
    private static (Window Window, IDisposable Restore) OpenOnSmallScreen(Func<Task> ask)
    {
        var before = Dialogs.ScreenRoom;
        Dialogs.ScreenRoom = (_, _) => SmallScreen;
        var opened = new List<Window>();
        using var watch = Window.WindowOpenedEvent.AddClassHandler(typeof(Window), (sender, _) => opened.Add((Window)sender!));
        _ = ask();
        var window = Assert.Single(opened);
        window.UpdateLayout();
        Layout();
        return (window, new Restore(() => Dialogs.ScreenRoom = before));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static void AssertInside(Control control, Window window)
    {
        var box = new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
        Assert.True(box.Top >= 0 && box.Bottom <= window.Bounds.Height + 0.5, $"{Screen.Describe(control)} at {box} is outside the window ({window.Bounds.Size})");
    }

    [AvaloniaFact]
    public async Task TheDeleteQuestion_WithAHugeList_FitsASmallScreen_AndItsButtonsCanBePressed()
    {
        Task<(bool Yes, bool Ticked)>? answer = null;
        var (window, restore) = OpenOnSmallScreen(() => answer = Dialogs.ConfirmWithTickAsync(null, "Pairnets – allow deletions?", HugeList(),
            "Delete 3340 files", "Cancel", "Do the same on my other computers", danger: true));
        using (restore)
        {
            Assert.True(window.Bounds.Height <= SmallScreen, $"the window is {window.Bounds.Height} high on a {SmallScreen} px screen");
            var yes = Screen.Find<Button>(window, "Delete 3340 files");
            var cancel = Screen.Find<Button>(window, "Cancel");
            var tick = Screen.All<CheckBox>(window).Single();
            AssertInside(yes, window);
            AssertInside(cancel, window);
            AssertInside(tick, window);

            // The text is in a scroll area that really scrolls, and wraps instead of widening the window.
            var scroller = Screen.All<ScrollViewer>(window).Single(s => s.Name == "DialogScroller");
            Assert.True(scroller.Extent.Height > scroller.Viewport.Height * 5, "a long list should need scrolling");
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5, "paths should wrap, not scroll sideways");

            tick.IsChecked = true;
            Person.Click(yes);
            Layout();
            var (pressed, ticked) = await answer!;
            Assert.True(pressed && ticked);
        }
    }

    [AvaloniaFact]
    public async Task APopUpWithManyLongPoints_FitsASmallScreen_AndKeepsItsButtons()
    {
        Task<string?>? answer = null;
        var points = Enumerable.Range(0, 400).Select(i => $"Folder {i}/" + new string('x', 150)).ToList();
        var (window, restore) = OpenOnSmallScreen(() => answer = Dialogs.ChooseAsync(null, "Move files?", "These files leave this computer.", points, "Cancel", "Move them"));
        using (restore)
        {
            Assert.True(window.Bounds.Height <= SmallScreen);
            AssertInside(Screen.Find<Button>(window, "Cancel"), window);
            AssertInside(Screen.Find<Button>(window, "Move them"), window);
            Person.Click(Screen.Find<Button>(window, "Move them"));
            Layout();
            Assert.Equal("Move them", await answer!);
        }
    }

    [AvaloniaFact]
    public async Task AShortQuestion_StillOpensSmall()
    {
        Task<bool>? answer = null;
        var (window, restore) = OpenOnSmallScreen(() => answer = Dialogs.ConfirmAsync(null, "Pairnets", "Open Pairnets?"));
        using (restore)
        {
            Assert.True(window.Bounds.Height < 250, $"a one-line question is {window.Bounds.Height} high");
            Person.Click(Screen.Find<Button>(window, "No"));
            Layout();
            Assert.False(await answer!);
        }
    }

    [AvaloniaFact]
    public void TheUpdateCard_InTheSidebar_KeepsItsTitleReadable_AndPutsTheButtonUnderIt()
    {
        var window = new MainWindow(new SampleActions()) { Width = 980, Height = 700 };
        window.Show();
        window.ShowUpdate("Pairnets 1.0.61.10 is ready", "Download it from the page.", UpdateTexts.OpenDownloadPage);
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        var title = Screen.All<TextBlock>(window).Single(t => t.Name == "UpdateTitle");
        var button = Screen.All<Button>(window).Single(b => b.Name == "UpdateButton");
        Assert.True(title.TextLayout.TextLines.Count <= 2, $"the title takes {title.TextLayout.TextLines.Count} lines");
        Assert.True(title.Bounds.Width > 100, $"the title is only {title.Bounds.Width} px wide");
        var titleBox = new Rect(title.Bounds.Size).TransformToAABB(title.TransformToVisual(window)!.Value);
        var buttonBox = new Rect(button.Bounds.Size).TransformToAABB(button.TransformToVisual(window)!.Value);
        Assert.True(buttonBox.Top >= titleBox.Bottom - 0.5, "the button should sit under the title");

        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        if (outDir is not null && window.CaptureRenderedFrame() is { } frame)
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, "update-card.png"));
        }
        window.AllowClose = true;
        window.Close();
    }

    /// <summary>Pictures of the deletion question on a small screen (written when PAIRNETS_SCREENSHOT_DIR is set).</summary>
    [AvaloniaFact]
    public void RenderTheDeleteQuestion()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        if (outDir is null)
            return;
        var (window, restore) = OpenOnSmallScreen(() => Dialogs.ConfirmWithTickAsync(null, "Pairnets – allow deletions?", HugeList(),
            "Delete 3340 files", "Cancel", "Do the same on my other computers", danger: true));
        using (restore)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Directory.CreateDirectory(outDir);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "delete-question.png"));
            window.Close();
        }
    }
}
