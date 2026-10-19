using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// Keyboard users see where they are: both apps draw the same 2 px ring in the dark accent ink (T.AccentInk) around
/// the control that has the keyboard focus (design review N1, 2026-10-19).
/// </summary>
public class FocusRingTests
{
    [AvaloniaFact]
    public void TabDrawsTheDarkBlueRingInTheMacLinuxApp()
    {
        var first = new Button { Content = "Sync now" };
        var round = new Button { Content = "x", Classes = { "RoundIconButton" } };
        var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { first, round } } };
        window.Show();

        foreach (var (button, radius) in new[] { (first, 14.0), (round, 999.0) })
        {
            button.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            var ring = Assert.Single(AdornerLayer.GetAdornerLayer(button)!.Children.OfType<Border>(), b => AdornerLayer.GetAdornedElement(b) == button);
            Assert.Equal(new Avalonia.Thickness(-3), ring.Margin);
            Assert.Equal(new Avalonia.Thickness(2), ring.BorderThickness);
            Assert.Equal(radius, ring.CornerRadius.TopLeft);
            Assert.True(window.TryFindResource("T.AccentInk", window.ActualThemeVariant, out var ink));
            Assert.Equal(((ISolidColorBrush)ink!).Color, Assert.IsAssignableFrom<ISolidColorBrush>(ring.BorderBrush).Color);
        }
        window.Close();
    }

    /// <summary>The Windows app: no control may switch the focus picture off; they all use the FocusRing styles.</summary>
    [Fact]
    public void NoWindowsControlHidesTheFocus()
    {
        var folder = RepoPaths.Of("src", "Pairnets.Client");
        var hidden = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"FocusVisualStyle(""\s*Value)?\s*=\s*(""\{x:Null\}""|null\b)"))
            .Select(f => Path.GetRelativePath(RepoPaths.Root, f))
            .ToList();
        Assert.True(hidden.Count == 0, "Use the FocusRing style instead of no focus picture in: " + string.Join(", ", hidden));
        Assert.Contains("x:Key=\"FocusRing\"", RepoPaths.Read("src", "Pairnets.Client", "Themes", "Controls.xaml"), StringComparison.Ordinal);
        Assert.Contains("T.AccentInk", RepoPaths.Read("src", "Pairnets.Client", "Themes", "Controls.xaml"), StringComparison.Ordinal);
    }
}
