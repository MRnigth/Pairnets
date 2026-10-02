using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tether.Core.Sync;

namespace Tether.Client.Platform;

/// <summary>Colored status icons drawn at runtime (no image assets to maintain).</summary>
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
                g.FillEllipse(fill, 3, 3, 26, 26);
                using var ring = new Pen(Color.White, 3);
                g.DrawEllipse(ring, 10, 10, 12, 12);
            }
            var handle = bmp.GetHicon();
            icon = (Icon)Icon.FromHandle(handle).Clone();
            DestroyIcon(handle);
            Cache[status] = icon;
            return icon;
        }
    }
}
