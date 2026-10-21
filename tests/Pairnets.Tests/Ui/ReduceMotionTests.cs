using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>The Mac/Linux app keeps still when the system asks for less motion, like the Windows app (design review N2).</summary>
public class ReduceMotionTests
{
    [AvaloniaFact]
    public void NoAnimationStartsWhileMotionIsReduced()
    {
        var glyph = new Border();
        try
        {
            Motion.Enabled = false;
            Motion.Set(glyph, "spin", true);
            Motion.Replay(glyph, "pop");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(glyph.Classes);
        }
        finally
        {
            Motion.Enabled = true;
        }
        Motion.Set(glyph, "spin", true);
        Assert.Contains("spin", glyph.Classes);
        Motion.Set(glyph, "spin", false);
        Assert.DoesNotContain("spin", glyph.Classes);
    }

    /// <summary>Animation classes are switched through Motion, so none starts behind its back.</summary>
    [Fact]
    public void TheViewsSwitchAnimationsThroughMotion()
    {
        string[] names = ["bob", "spin", "spin-slow", "ripple", "pulse", "pop", "rise", "fall", "fall-late"];
        var direct = Directory.EnumerateFiles(RepoPaths.Of("src", "Pairnets.Desktop", "Views"), "*.cs")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => names.Any(n => l.Text.Contains($"Classes.Set(\"{n}\"", StringComparison.Ordinal) || l.Text.Contains($"Classes.Add(\"{n}\"", StringComparison.Ordinal)))
            .Where(l => l.File != "Motion.cs")
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();
        Assert.True(direct.Count == 0, "Use Motion.Set / Motion.Replay at: " + string.Join(", ", direct));
    }
}
