using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// 1.0.61.13 gated the endless animations with "Window.live :is(Control).spin": every such control ever built stayed
/// subscribed to the window's classes, so a window redrawing through a 50,000-file sync held 1.8 GB. Rows are rebuilt
/// all the time; once they leave the window they must be collectable.
/// </summary>
[Collection(AvaloniaUi.Name)]
public class MotionMemoryTests
{
    /// <summary>
    /// The real app with its window open syncs 3,000 small files. The window redraws several times a second meanwhile;
    /// with the old selectors the rows of every redraw stayed in memory (about 170 MB live here, 80 MB without).
    /// </summary>
    [AvaloniaFact]
    public async Task AWindowRedrawingThroughABigSyncDoesNotHoldEveryRowItEverBuilt()
    {
        const int files = 3000;
        await using var app = await AppUnderTest.StartAsync();
        for (var d = 0; d < 100; d++)
        {
            var dir = Path.Combine(app.Folder, "dir" + d);
            Directory.CreateDirectory(dir);
            for (var f = 0; f < files / 100; f++)
                File.WriteAllText(Path.Combine(dir, $"file{f}.txt"), $"content {d}/{f} " + new string('x', 150));
        }
        app.App.ShowMainWindow();
        var until = DateTime.UtcNow.AddMinutes(3);
        while ((await app.NestFilesAsync()).Count < files)
        {
            Assert.True(DateTime.UtcNow < until, "the files did not reach the nest");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        for (var i = 0; i < 40; i++) // the last redraws
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        var liveMb = GC.GetTotalMemory(true) / 1048576;
        Assert.True(liveMb < 125, $"{liveMb} MB are still in use after the sync (about 80 without the leak, 170 with it)");
    }

    /// <summary>The endless animations are gated by a class on the element itself, never by a class of the window (see above).</summary>
    [Fact]
    public void TheMotionStylesDoNotDependOnAClassOfTheWindow()
    {
        var styles = File.ReadAllText(RepoPaths.Of("src", "Pairnets.Desktop", "Styles", "Controls.axaml"));
        Assert.DoesNotContain("Selector=\"Window.", styles);
        Assert.Contains("Selector=\":is(Control).spin.go\"", styles);
    }

    [AvaloniaFact]
    public void AnAnimatedControlRunsOnlyWhileMotionIsLive()
    {
        var window = new Window { Content = new Border() };
        window.Show();
        var spinner = new Border();
        ((Border)window.Content).Child = spinner;
        Motion.Set(spinner, "spin", true);
        Assert.Contains("go", spinner.Classes);
        Motion.Set(spinner, "spin", false);
        Assert.DoesNotContain("spin", spinner.Classes);
        window.Close();
    }
}
