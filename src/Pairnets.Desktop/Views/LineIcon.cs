using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pairnets.Desktop.Views;

/// <summary>
/// Draws a 24x24 line-icon geometry scaled to the control's size, so every icon keeps the same
/// proportions (Path with Stretch would blow up thin icons like "!").
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<LineIcon, IBrush?>(nameof(Stroke), Brushes.Black);
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 2);

    static LineIcon() => AffectsRender<LineIcon>(DataProperty, StrokeProperty, StrokeThicknessProperty);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null || Stroke is null)
            return;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;
        var scale = size / 24.0;
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var pen = new Pen(Stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            context.DrawGeometry(null, pen, Data);
    }
}
