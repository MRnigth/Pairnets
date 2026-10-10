using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The round status badge of the main window and the tray panel (same as the Mac/Linux app). While syncing it shows
/// what this computer really does: a calm turn while it checks for changes, an arrow rising while it uploads or
/// falling while it downloads, both taking turns while it does both, and a still, faded arrow on orange when the
/// transfer stalled. It breathes while waiting for the other computer.
/// </summary>
internal static class Badge
{
    /// <summary>Draws <paramref name="s"/> on the badge and returns its colour (for the tint behind it).</summary>
    public static string Show(StatusSnapshot s, Ellipse badge, LineIcon glyph, LineIcon second, double size, bool animate)
    {
        var (brush, icon) = Visuals.ForBadge(s);
        badge.SetResourceReference(Shape.FillProperty, brush);
        glyph.Data = Visuals.Resource<Geometry>(icon);
        var motion = s.IsWaiting ? SyncMotion.None : s.Motion;
        var both = motion == SyncMotion.Both;
        second.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        glyph.Width = glyph.Height = second.Width = second.Height = both ? Math.Round(size * 0.85) : size;
        glyph.Margin = both ? new Thickness(0, 0, size * 0.42, 0) : new Thickness(0);
        second.Margin = both ? new Thickness(size * 0.42, 0, 0, 0) : new Thickness(0);
        glyph.Opacity = motion == SyncMotion.Stalled ? 0.6 : 1;
        Motion.Spin(glyph, animate && motion == SyncMotion.Checking, slow: true);
        Motion.Travel(glyph, !animate ? null : motion is SyncMotion.Up or SyncMotion.Both ? true : motion == SyncMotion.Down ? false : null);
        Motion.Travel(second, animate && both ? false : null, late: true);
        return brush;
    }
}
