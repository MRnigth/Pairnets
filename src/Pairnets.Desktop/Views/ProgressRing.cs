using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The overview's ring: a track all the way round, and an arc from the top, clockwise, for <see cref="Value"/>
/// (0–100). At 100 it is a closed circle. Drawn here (not a Path) so the arc keeps round ends at any size.
/// </summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 8);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> RingBrushProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(RingBrush));

    static ProgressRing() => AffectsRender<ProgressRing>(ValueProperty, ThicknessProperty, TrackBrushProperty, RingBrushProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? RingBrush
    {
        get => GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= Thickness * 2)
            return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = (size - Thickness) / 2;
        if (TrackBrush is not null)
            context.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, r, r);
        if (RingBrush is null)
            return;
        var value = Math.Clamp(Value, 0, 100);
        if (value >= 99.95)
        {
            context.DrawEllipse(null, new Pen(RingBrush, Thickness), center, r, r);
            return;
        }
        if (value <= 0.05)
            return;
        var angle = value / 100 * 2 * Math.PI;
        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(r, r), 0, value > 50, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(RingBrush, Thickness, lineCap: PenLineCap.Round), arc);
    }
}
