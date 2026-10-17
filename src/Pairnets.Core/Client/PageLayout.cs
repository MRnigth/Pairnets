namespace Pairnets.Core.Client;

/// <summary>
/// How the pages fit a narrow window, the same in both apps. The Mac/Linux window opens at 980 px, which leaves the
/// page about 600 px beside the left side; the Windows one can be made that narrow too.
/// </summary>
public static class PageLayout
{
    /// <summary>The Activity page's search box, beside the filter buttons when they fit together.</summary>
    public const double SearchWidth = 250;

    /// <summary>The space between the filter buttons and the search box.</summary>
    public const double SearchGap = 12;

    /// <summary>A computer card on the Devices page is never narrower than this (narrower cut off "Linux · 1.0…").</summary>
    public const double MinCardWidth = 250;

    /// <summary>A fact about the nest on the Devices page ("Keeps deleted files") is never narrower than this.</summary>
    public const double MinChipWidth = 170;

    /// <summary>The nest's four facts on the Devices page: 4 across, or 2 by 2 when 4 would cut them off.</summary>
    public static int ChipColumns(double width) => width / 4 >= MinChipWidth ? 4 : 2;

    /// <summary>How many computer cards fit side by side in <paramref name="width"/> (1 to 3).</summary>
    public static int CardColumns(double width) => Math.Clamp((int)(width / MinCardWidth), 1, 3);

    /// <summary>
    /// True when the search box goes on its own line under the filter buttons: beside them it would push them onto
    /// two lines.
    /// </summary>
    public static bool SearchGoesBelow(double filtersWidth, double width) => filtersWidth + SearchGap + SearchWidth > width;
}
