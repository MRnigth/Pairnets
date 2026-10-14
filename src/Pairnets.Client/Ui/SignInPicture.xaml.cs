using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Pairnets.Client.Ui;

/// <summary>What the nest picture shows: nothing linked yet, linked, or no answer.</summary>
internal enum NestPictureKind
{
    /// <summary>Waiting for a name: dashed lines, the nest in the accent.</summary>
    Start,

    /// <summary>The nest was found: a green line, a green check.</summary>
    Found,

    /// <summary>No answer: a broken red line, the nest dimmed.</summary>
    NoAnswer,
}

/// <summary>The soft colours of a sign-in picture (the design's tones).</summary>
internal enum PictureTone
{
    Accent,
    Ok,
    Warn,
    Bad,
    Plain,
}

/// <summary>Icons the sign-in and update windows draw that the shared icon set does not have (24×24 line paths).</summary>
internal static class StepIcons
{
    public static readonly Geometry Arrow = Geometry.Parse("M5,12 L19,12 M13,6 L19,12 L13,18");
    public static readonly Geometry Shield = Geometry.Parse("M12,3 L5,6 L5,11 C5,15.5 8,19.3 12,21 C16,19.3 19,15.5 19,11 L19,6 Z M9,12 L11,14 L15,10");
    public static readonly Geometry Globe = Geometry.Parse("M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 M3,12 L21,12 M12,3 C16.5,7 16.5,17 12,21 C7.5,17 7.5,7 12,3");
    public static readonly Geometry Mail = Geometry.Parse("M5.5,5 L18.5,5 A2.5,2.5 0 0 1 21,7.5 L21,16.5 A2.5,2.5 0 0 1 18.5,19 L5.5,19 A2.5,2.5 0 0 1 3,16.5 L3,7.5 A2.5,2.5 0 0 1 5.5,5 Z M4,7 L12,13 L20,7");
    public static readonly Geometry User = Geometry.Parse("M2.5,12 A9.5,9.5 0 1 1 21.5,12 A9.5,9.5 0 1 1 2.5,12 M9,10 A3,3 0 1 1 15,10 A3,3 0 1 1 9,10 M6.5,18.5 A6.5,6.5 0 0 1 17.5,18.5");
    public static readonly Geometry External = Geometry.Parse("M14,4 L20,4 L20,10 M20,4 L11,13 M19,14 L19,18 A2,2 0 0 1 17,20 L6,20 A2,2 0 0 1 4,18 L4,7 A2,2 0 0 1 6,5 L10,5");
    public static readonly Geometry Copy = Geometry.Parse("M11,9 L18,9 A2,2 0 0 1 20,11 L20,18 A2,2 0 0 1 18,20 L11,20 A2,2 0 0 1 9,18 L9,11 A2,2 0 0 1 11,9 Z M15,9 L15,6 A2,2 0 0 0 13,4 L6,4 A2,2 0 0 0 4,6 L4,13 A2,2 0 0 0 6,15 L9,15");
    public static readonly Geometry Hourglass = Geometry.Parse("M6,3 L18,3 M6,21 L18,21 M7.5,3 L7.5,5.5 C7.5,7.5 9,9 12,12 C15,9 16.5,7.5 16.5,5.5 L16.5,3 M7.5,21 L7.5,18.5 C7.5,16.5 9,15 12,12 C15,15 16.5,16.5 16.5,18.5 L16.5,21");
    public static readonly Geometry Denied = Geometry.Parse("M3,12 A9,9 0 1 1 21,12 A9,9 0 1 1 3,12 M5.7,5.7 L18.3,18.3");
    public static readonly Geometry Retry = Geometry.Parse("M4,12 A8,8 0 1 0 6.4,6.3 L4,8.5 M4,4 L4,8.5 L8.5,8.5");
    public static readonly Geometry Power = Geometry.Parse("M12,3 L12,11 M7,6.3 A8,8 0 1 0 17,6.3");
    public static readonly Geometry Sync = Geometry.Parse("M20,11 A8,8 0 0 0 5.7,6.1 L4,8 M4,3 L4,8 L9,8 M4,13 A8,8 0 0 0 18.3,17.9 L20,16 M20,21 L20,16 L15,16");
    public static readonly Geometry Lock = Geometry.Parse("M6,11 L18,11 A1.5,1.5 0 0 1 19.5,12.5 L19.5,19 A1.5,1.5 0 0 1 18,20.5 L6,20.5 A1.5,1.5 0 0 1 4.5,19 L4.5,12.5 A1.5,1.5 0 0 1 6,11 Z M8,11 L8,8 A4,4 0 0 1 16,8 L16,11");
}

/// <summary>The picture at the top of each sign-in step.</summary>
public partial class SignInPicture : UserControl
{
    public SignInPicture() => InitializeComponent();

    /// <summary>Points a property at a theme brush, so it follows light and dark.</summary>
    private static void Use(FrameworkElement element, DependencyProperty property, string key) => element.SetResourceReference(property, key);

    internal static (string Halo, string Ink, string Solid) Colours(PictureTone tone) => tone switch
    {
        PictureTone.Ok => ("T.OkPill", "T.OkText", "T.Ok"),
        PictureTone.Warn => ("T.WarnPill", "T.WarnText", "T.Warn"),
        PictureTone.Bad => ("T.BadPill", "T.BadText", "T.Bad"),
        PictureTone.Plain => ("T.Chip", "T.Muted", "T.Off"),
        _ => ("T.AccentSoft", "T.AccentInk", "T.AccentButton"),
    };

    /// <summary>The computer, the link and the nest (and the other computers).</summary>
    internal void ShowNest(NestPictureKind kind)
    {
        NestRow.Visibility = Visibility.Visible;
        IconBox.Visibility = Visibility.Collapsed;
        var found = kind == NestPictureKind.Found;
        var none = kind == NestPictureKind.NoAnswer;
        LinkSolid.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        LinkBroken.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        LinkDashed.Visibility = kind == NestPictureKind.Start ? Visibility.Visible : Visibility.Collapsed;
        OtherLink.Visibility = OtherColumn.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        NestBadge.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        NestLabel.Text = none ? "No answer" : "Your nest";
        var (halo, ink, _) = Colours(found ? PictureTone.Ok : none ? PictureTone.Plain : PictureTone.Accent);
        Use(NestHalo, Shape.FillProperty, halo);
        Use(NestIcon, LineIcon.StrokeProperty, none ? "T.TextSoft" : ink);
        if (none)
            NestTile.Background = Brushes.Transparent;
        else
            Use(NestTile, Border.BackgroundProperty, "T.Card");
        Use(NestTile, Border.BorderBrushProperty, none ? "T.Dash" : "T.Border");
    }

    /// <summary>One soft circle with an icon, and a small badge in its corner (an icon, or short text such as "0:00").</summary>
    internal void ShowIcon(Geometry glyph, PictureTone tone, string? glyphBrush = null, Geometry? badge = null, PictureTone badgeTone = PictureTone.Accent, string? badgeText = null)
    {
        NestRow.Visibility = Visibility.Collapsed;
        IconBox.Visibility = Visibility.Visible;
        var (halo, ink, _) = Colours(tone);
        Use(IconHalo, Shape.FillProperty, halo);
        IconGlyph.Data = glyph;
        Use(IconGlyph, LineIcon.StrokeProperty, glyphBrush ?? ink);
        var has = badge is not null || badgeText is not null;
        IconBadge.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
            return;
        Use(IconBadge, Border.BackgroundProperty, Colours(badgeTone).Solid);
        IconBadgeGlyph.Data = badge;
        IconBadgeGlyph.Visibility = badge is null ? Visibility.Collapsed : Visibility.Visible;
        IconBadgeText.Text = badgeText ?? string.Empty;
        IconBadgeText.Visibility = badgeText is null ? Visibility.Collapsed : Visibility.Visible;
        if (badgeTone == PictureTone.Warn)
            Use(IconBadgeText, TextBlock.ForegroundProperty, "T.NeedsActiveText");
        else
            IconBadgeText.Foreground = Brushes.White;
    }
}
