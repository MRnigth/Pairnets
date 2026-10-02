using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tether.Core.Sync;

namespace Tether.Client.Platform;

/// <summary>
/// Colored status circles with a white glyph (check, arrows, pause, !, x, cloud), drawn at runtime
/// so they stay sharp at 16 px and need no image assets.
/// </summary>
public static class TrayIcons
{
    private static readonly Dictionary<RunnerStatus, Icon> Cache = [];

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon For(RunnerStatus status)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(status, out var icon))
                return icon;
            var color = status switch
            {
                RunnerStatus.Idle => Color.FromArgb(46, 160, 67),
                RunnerStatus.Syncing => Color.FromArgb(33, 118, 214),
                RunnerStatus.Offline => Color.FromArgb(128, 128, 128),
                RunnerStatus.Paused => Color.FromArgb(214, 160, 33),
                RunnerStatus.Blocked => Color.FromArgb(230, 110, 20),
                _ => Color.FromArgb(207, 34, 46),
            };
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, 1, 1, 30, 30);
                DrawGlyph(g, status);
            }
            var handle = bmp.GetHicon();
            icon = (Icon)Icon.FromHandle(handle).Clone();
            DestroyIcon(handle);
            Cache[status] = icon;
            return icon;
        }
    }

    /// <summary>Same shapes as the window icons (Themes/Icons.xaml), on a 32 px canvas with a 4 px margin.</summary>
    private static void DrawGlyph(Graphics g, RunnerStatus status)
    {
        using var pen = new Pen(Color.White, 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var path = new GraphicsPath();
        static PointF P(float x, float y) => new(4 + x, 4 + y);
        switch (status)
        {
            case RunnerStatus.Idle:
                path.AddLines([P(6, 12.5f), P(10.5f, 17), P(18.5f, 7.5f)]);
                break;
            case RunnerStatus.Syncing:
                path.AddArc(4 + 5.5f, 4 + 5.5f, 13, 13, -40, 290);
                g.DrawPath(pen, path);
                path.Reset();
                path.AddLines([P(18.5f, 4.5f), P(18.5f, 9), P(14, 9)]);
                break;
            case RunnerStatus.Paused:
                g.DrawLine(pen, P(9.5f, 7), P(9.5f, 17));
                g.DrawLine(pen, P(14.5f, 7), P(14.5f, 17));
                return;
            case RunnerStatus.Blocked:
                g.DrawLine(pen, P(12, 6), P(12, 13));
                using (var dot = new SolidBrush(Color.White))
                    g.FillEllipse(dot, P(12, 17.5f).X - 2.2f, P(12, 17.5f).Y - 2.2f, 4.4f, 4.4f);
                return;
            case RunnerStatus.Offline:
                g.DrawLine(pen, P(7, 12), P(17, 12));
                return;
            default:
                g.DrawLine(pen, P(7.5f, 7.5f), P(16.5f, 16.5f));
                g.DrawLine(pen, P(16.5f, 7.5f), P(7.5f, 16.5f));
                return;
        }
        g.DrawPath(pen, path);
    }
}
