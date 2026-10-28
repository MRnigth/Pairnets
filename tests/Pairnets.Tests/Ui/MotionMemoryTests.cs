using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// 1.0.61.13 gated the endless animations with "Window.live :is(Control).spin": every such control ever built stayed
/// subscribed to the window's classes, so a window redrawing through a 50,000-file sync held 1.8 GB. The leak builds up
/// with redraws over minutes, so it is guarded here by the styles' shape; the memory itself is measured with
/// UploadMemoryUiProbe (PAIRNETS_MEMPROBE=1, see docs/DECISIONS.md).
/// </summary>
[Collection(AvaloniaUi.Name)]
public class MotionMemoryTests
{
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
