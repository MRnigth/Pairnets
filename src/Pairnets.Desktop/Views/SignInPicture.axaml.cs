using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;

namespace Pairnets.Desktop.Views;

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

/// <summary>The picture at the top of each sign-in step.</summary>
public partial class SignInPicture : UserControl
{
    public SignInPicture() => InitializeComponent();

    /// <summary>Points a property at a theme brush, so it follows light and dark.</summary>
    private static void Use(Control element, AvaloniaProperty property, string key) =>
        element.Bind(property, element.GetResourceObservable(key));

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
        NestRow.IsVisible = true;
        IconBox.IsVisible = false;
        var found = kind == NestPictureKind.Found;
        var none = kind == NestPictureKind.NoAnswer;
        LinkSolid.IsVisible = found;
        LinkBroken.IsVisible = none;
        LinkDashed.IsVisible = kind == NestPictureKind.Start;
        OtherLink.IsVisible = OtherColumn.IsVisible = !none;
        NestBadge.IsVisible = found;
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
        NestRow.IsVisible = false;
        IconBox.IsVisible = true;
        var (halo, ink, _) = Colours(tone);
        Use(IconHalo, Shape.FillProperty, halo);
        IconGlyph.Data = glyph;
        Use(IconGlyph, LineIcon.StrokeProperty, glyphBrush ?? ink);
        var has = badge is not null || badgeText is not null;
        IconBadge.IsVisible = has;
        if (!has)
            return;
        Use(IconBadge, Border.BackgroundProperty, Colours(badgeTone).Solid);
        IconBadgeGlyph.Data = badge;
        IconBadgeGlyph.IsVisible = badge is not null;
        IconBadgeText.Text = badgeText ?? string.Empty;
        IconBadgeText.IsVisible = badgeText is not null;
        if (badgeTone == PictureTone.Warn)
            Use(IconBadgeText, TextBlock.ForegroundProperty, "T.NeedsActiveText");
        else
            IconBadgeText.Foreground = Brushes.White;
    }
}
