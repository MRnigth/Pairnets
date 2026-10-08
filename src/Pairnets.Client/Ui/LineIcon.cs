using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pairnets.Client.Ui;

/// <summary>
/// Draws a 24x24 line-icon geometry scaled to the element's size, so every icon keeps the same
/// proportions (a Path with Stretch would blow up thin icons like "!"). Same icons as the Mac/Linux app.
/// Without a <see cref="Stroke"/> it draws in the text colour around it, so an icon in a button
/// follows the button (ink on the dark "Sync now", red in "Sign out", muted in the rail).
/// </summary>
public sealed class LineIcon : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(LineIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LineIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LineIcon), new FrameworkPropertyMetadata(1.9, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The inherited text colour, used when no <see cref="Stroke"/> is set.</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LineIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    static LineIcon()
    {
        // 16 × 16 unless a style or the element says otherwise (as the Mac/Linux app's "icon" class).
        WidthProperty.OverrideMetadata(typeof(LineIcon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(LineIcon), new FrameworkPropertyMetadata(16.0));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var stroke = Stroke ?? Foreground;
        if (Data is null || stroke is null)
            return;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
            return;
        var scale = size / 24.0;
        var pen = new Pen(stroke, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(transform);
        dc.DrawGeometry(null, pen, Data);
        dc.Pop();
    }
}
