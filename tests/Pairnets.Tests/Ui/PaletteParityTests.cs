using System.Globalization;
using System.Xml.Linq;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The Windows app (Themes/Light.xaml, Dark.xaml, Icons.xaml) and the Mac/Linux app (Styles/Palette.axaml) share every
/// colour: the same key has the same hex value in both. This reads both palettes as text, so it runs on every system,
/// and also checks the contrast of the pairs the design review fixed.
/// </summary>
public class PaletteParityTests
{
    /// <summary>The same colour under another name: Windows name, Mac/Linux name.</summary>
    private static readonly (string Wpf, string Avalonia)[] Renamed = [("T.DangerBtn", "T.DangerButton")];

    /// <summary>Colours only the Mac/Linux app uses (the Windows app draws these places with other keys).</summary>
    private static readonly string[] OnlyAvalonia = ["T.Panel", "T.Code"];

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, string> Brushes(XElement dictionary) => dictionary.Elements()
        .Where(e => e.Name.LocalName == "SolidColorBrush")
        .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => ((string)e.Attribute("Color")!).ToUpperInvariant(), StringComparer.Ordinal);

    private static Dictionary<string, string> Wpf(string theme)
    {
        var colours = Brushes(XDocument.Load(RepoPaths.Of("src", "Pairnets.Client", "Themes", theme + ".xaml")).Root!);
        foreach (var (key, value) in Brushes(XDocument.Load(RepoPaths.Of("src", "Pairnets.Client", "Themes", "Icons.xaml")).Root!))
            colours[key] = value;
        foreach (var (wpf, avalonia) in Renamed)
        {
            if (colours.Remove(wpf, out var value))
                colours[avalonia] = value;
        }
        return colours;
    }

    private static Dictionary<string, string> Avalonia(string theme)
    {
        var root = XDocument.Load(RepoPaths.Of("src", "Pairnets.Desktop", "Styles", "Palette.axaml")).Root!;
        var themed = root.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(X + "Key") == theme);
        var colours = Brushes(themed);
        foreach (var (key, value) in Brushes(root))
            colours[key] = value;
        return colours;
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void BothAppsHaveTheSameColours(string theme)
    {
        var wpf = Wpf(theme);
        var avalonia = Avalonia(theme);
        var problems = wpf.Keys.Except(avalonia.Keys).Select(k => $"{k} is in the Windows app ({theme}) but not in Palette.axaml.")
            .Concat(avalonia.Keys.Except(wpf.Keys).Except(OnlyAvalonia).Select(k => $"{k} is in Palette.axaml ({theme}) but not in the Windows app."))
            .Concat(wpf.Keys.Intersect(avalonia.Keys).Where(k => wpf[k] != avalonia[k])
                .Select(k => $"{k} ({theme}) is {wpf[k]} in the Windows app but {avalonia[k]} in the Mac/Linux app."))
            .ToList();
        Assert.True(problems.Count == 0, "Change both palettes together:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.Contains("T.AccentInk", avalonia.Keys); // the files were read at all
    }

    /// <summary>
    /// The pairs the design review (2026-10-19) found too faint, at WCAG 2 contrast: 4.5:1 for words, 3:1 for the
    /// switch track, the focus ring and status dots. Colours with alpha are mixed over the surface first.
    /// </summary>
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TheFixedPairsAreEasyToSee(string theme)
    {
        var c = Avalonia(theme);
        var card = c["T.Card"];
        var problems = new List<string>();
        void Check(string what, string fore, string back, double min)
        {
            var ratio = Contrast(Over(fore, back), Over(back, "#FFFFFF"));
            if (ratio < min)
                problems.Add($"{what} ({theme}): {ratio:0.00}:1, needs {min}:1");
        }
        Check("switch track off on a card", c["T.SwitchOff"], card, 3);
        Check("switch knob off on its track", c["T.KnobOff"], c["T.SwitchOff"], 3);
        Check("focus ring on a card", c["T.AccentInk"], card, 3);
        Check("focus ring on the page", c["T.AccentInk"], c["T.Bg"], 3);
        Check("white on a hovered blue button", "#FFFFFF", c["T.AccentButtonHover"], 4.5);
        Check("white on the orange Fix button", "#FFFFFF", c["S.OrangeButton"], 4.5);
        Check("white on the hovered Fix button", "#FFFFFF", c["S.OrangeButtonHover"], 4.5);
        Check("soft text on a card", c["T.TextSoft"], card, 4.5);
        Check("soft text on a chosen row", c["T.TextSoft"], Over(c["T.AccentSoft"], card), 4.5);
        Check("amber dot on a card", c["T.Warn"], card, 3);
        Check("grey dot on a card", c["T.Off"], card, 3);
        Check("dark pause sign on the yellow tray icon", "#2B1A00", c["S.Yellow"], 4.5);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>#RRGGBB or #AARRGGBB drawn over an opaque #RRGGBB.</summary>
    private static string Over(string colour, string back)
    {
        var f = Parse(colour);
        var b = Parse(back);
        var a = f.A / 255.0;
        int Mix(int fc, int bc) => (int)Math.Round(fc * a + bc * (1 - a));
        return $"#{Mix(f.R, b.R):X2}{Mix(f.G, b.G):X2}{Mix(f.B, b.B):X2}";
    }

    private static (int A, int R, int G, int B) Parse(string hex)
    {
        var h = hex.TrimStart('#');
        var v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return h.Length == 8 ? ((int)(v >> 24), (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255) : (255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255);
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            var (_, r, g, b) = Parse(hex);
            static double Lin(int c) => c / 255.0 <= 0.04045 ? c / 255.0 / 12.92 : Math.Pow((c / 255.0 + 0.055) / 1.055, 2.4);
            return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
        }
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
