using System.Windows;
using System.Windows.Media;

namespace Pairnets.Client.Ui;

/// <summary>
/// The overview's ring: a track all the way round, and an arc from the top, clockwise, for <see cref="Value"/>
/// (0–100). At 100 it is a closed circle. Drawn here (not a Path) so the arc keeps round ends at any size.
/// Same as the Mac/Linux app.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ProgressRing()
    {
        SetResourceReference(TrackBrushProperty, "T.Track");
        SetResourceReference(RingBrushProperty, "S.Blue");
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    /// <summary>Moves the ring to <paramref name="value"/> in a short glide instead of a jump.</summary>
    public void GlideTo(double value) => Motion.GlideTo(this, ValueProperty, value, 0.45);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (size - Thickness) / 2;
        if (TrackBrush is not null)
            dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, r, r);
        if (RingBrush is null)
            return;
        var value = Math.Clamp(Value, 0, 100);
        if (value >= 99.95)
        {
            dc.DrawEllipse(null, new Pen(RingBrush, Thickness), center, r, r);
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
            g.BeginFigure(start, false, false);
            g.ArcTo(end, new Size(r, r), 0, value > 50, SweepDirection.Clockwise, true, true);
        }
        arc.Freeze();
        dc.DrawGeometry(null, new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
}
