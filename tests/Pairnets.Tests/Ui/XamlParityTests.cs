using System.Text.RegularExpressions;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Ui;

/// <summary>Reads event handler names out of XAML text.</summary>
public static class XamlText
{
    /// <summary>The handler names in a XAML text (Click="OnSyncNow" gives OnSyncNow), comments left out.</summary>
    public static IReadOnlyList<string> HandlerNames(string xaml) =>
        Regex.Matches(Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline), @"\s[A-Za-z]+=""(On[A-Za-z0-9]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Each view of a folder ("MainWindow") with the handlers its XAML names.</summary>
    public static Dictionary<string, IReadOnlyList<string>> Views(string folder, string extension) =>
        Directory.EnumerateFiles(folder, "*" + extension)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => HandlerNames(File.ReadAllText(f)), StringComparer.Ordinal);
}

/// <summary>
/// The Windows app (WPF) and the Mac/Linux app (Avalonia) mirror each other window for window: the same windows,
/// and behind each the same buttons, menus and fields with the same handler names. This reads both apps' XAML as
/// text, so it runs on every system.
/// </summary>
public class XamlParityTests
{
    /// <summary>Differences on purpose: the view, the handler, the one app that has it, and why the other does not.</summary>
    private static readonly (string View, string Handler, string OnlyIn, string Why)[] Intended =
    [
        // WPF opens the "…" menus from a click handler; in Avalonia the button carries a MenuFlyout instead.
        ("MainWindow", "OnMore", WindowsApp, "the \"…\" menu opens from code"),
        ("TrayPanel", "OnMore", WindowsApp, "the \"…\" menu opens from code"),
        // WPF fades the rows in from code; Avalonia does it with the "enter" style class.
        ("MainWindow", "OnRowLoaded", WindowsApp, "rows fade in from code"),
        // Avalonia's menu takes the focus from the tray panel, which hides when it loses focus; the panel stays
        // open while its own menu is open. WPF's ContextMenu does not take the focus.
        ("TrayPanel", "OnMenuOpened", MacLinuxApp, "the panel stays open while its menu is open"),
    ];

    private const string WindowsApp = "the Windows app";
    private const string MacLinuxApp = "the Mac/Linux app";

    private static Dictionary<string, IReadOnlyList<string>> WpfViews => XamlText.Views(RepoPaths.Of("src", "Pairnets.Client", "Ui"), ".xaml");

    private static Dictionary<string, IReadOnlyList<string>> AvaloniaViews => XamlText.Views(RepoPaths.Of("src", "Pairnets.Desktop", "Views"), ".axaml");

    [Fact]
    public void BothAppsHaveTheSameWindows()
    {
        var wpf = WpfViews.Keys.ToHashSet();
        var avalonia = AvaloniaViews.Keys.ToHashSet();
        var problems = wpf.Except(avalonia).Select(v => $"{v} is in {WindowsApp} (src/Pairnets.Client/Ui/{v}.xaml) but not in {MacLinuxApp}.")
            .Concat(avalonia.Except(wpf).Select(v => $"{v} is in {MacLinuxApp} (src/Pairnets.Desktop/Views/{v}.axaml) but not in {WindowsApp}."))
            .ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Contains("MainWindow", avalonia); // the folders were read at all
    }

    [Fact]
    public void BothAppsHaveTheSameButtonsBehindEachWindow()
    {
        var wpf = WpfViews;
        var avalonia = AvaloniaViews;
        var problems = new List<string>();
        foreach (var view in wpf.Keys.Intersect(avalonia.Keys).Order(StringComparer.Ordinal))
        {
            foreach (var handler in wpf[view].Except(avalonia[view]).Where(h => !IsIntended(view, h, WindowsApp)))
                problems.Add($"{view}: {handler} is in {WindowsApp} ({view}.xaml) but not in {MacLinuxApp} ({view}.axaml).");
            foreach (var handler in avalonia[view].Except(wpf[view]).Where(h => !IsIntended(view, h, MacLinuxApp)))
                problems.Add($"{view}: {handler} is in {MacLinuxApp} ({view}.axaml) but not in {WindowsApp} ({view}.xaml).");
        }
        Assert.True(problems.Count == 0, "Change both apps together, or add the difference to the list at the top of XamlParityTests with the reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
        Assert.Contains("OnSyncNow", avalonia["MainWindow"]);
    }

    /// <summary>A difference that is no longer there comes off the list, so the list stays true.</summary>
    [Fact]
    public void TheListOfIntendedDifferencesIsStillTrue()
    {
        var wpf = WpfViews;
        var avalonia = AvaloniaViews;
        var stale = Intended
            .Where(d => !(d.OnlyIn == WindowsApp
                ? wpf.GetValueOrDefault(d.View, []).Contains(d.Handler) && !avalonia.GetValueOrDefault(d.View, []).Contains(d.Handler)
                : avalonia.GetValueOrDefault(d.View, []).Contains(d.Handler) && !wpf.GetValueOrDefault(d.View, []).Contains(d.Handler)))
            .Select(d => $"{d.View}: {d.Handler} is no longer only in {d.OnlyIn} ({d.Why}); take it off the list in XamlParityTests.")
            .ToList();
        Assert.True(stale.Count == 0, string.Join(Environment.NewLine, stale));
    }

    private static bool IsIntended(string view, string handler, string onlyIn) =>
        Intended.Any(d => d.View == view && d.Handler == handler && d.OnlyIn == onlyIn);
}
