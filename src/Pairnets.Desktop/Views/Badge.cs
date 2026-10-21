using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The round status badge of the main window and the tray panel (same as the Windows app). While syncing it shows
/// what this computer really does: a calm turn while it checks for changes, an arrow rising while it uploads or
/// falling while it downloads, both taking turns while it does both, and a still, faded arrow on orange when the
/// transfer stalled.
/// </summary>
internal static class Badge
{
    /// <summary>Draws <paramref name="s"/> on the badge and returns its colour (for the tint behind it).</summary>
    public static string Show(StatusSnapshot s, Ellipse badge, LineIcon glyph, LineIcon second, double size)
    {
        var (brush, icon) = Visuals.ForBadge(s);
        badge.Fill = Visuals.Resource<IBrush>(brush);
        glyph.Data = Visuals.Resource<Geometry>(icon);
        var motion = s.IsWaiting ? SyncMotion.None : s.Motion;
        var both = motion == SyncMotion.Both;
        second.IsVisible = both;
        glyph.Width = glyph.Height = second.Width = second.Height = both ? Math.Round(size * 0.85) : size;
        glyph.Margin = both ? new Thickness(0, 0, size * 0.42, 0) : default;
        second.Margin = both ? new Thickness(size * 0.42, 0, 0, 0) : default;
        glyph.Opacity = motion == SyncMotion.Stalled ? 0.6 : 1;
        Motion.Set(glyph, "spin-slow", motion == SyncMotion.Checking);
        Motion.Set(glyph, "rise", motion is SyncMotion.Up or SyncMotion.Both);
        Motion.Set(glyph, "fall", motion == SyncMotion.Down);
        Motion.Set(second, "fall-late", both);
        return brush;
    }
}
