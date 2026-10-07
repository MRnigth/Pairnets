using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The line between a computer and the server. Solid while connected, dashed when not, and dots
/// travel along it while files move: green towards the server, blue away from it. The "flowing"
/// style class animates <see cref="Phase"/> (0 → 1), which moves the dots.
/// </summary>
public sealed class FlowLine : Control
{
    public static readonly StyledProperty<LinkFlow> FlowProperty = AvaloniaProperty.Register<FlowLine, LinkFlow>(nameof(Flow));
    public static readonly StyledProperty<bool> LinkedProperty = AvaloniaProperty.Register<FlowLine, bool>(nameof(Linked));
    public static readonly StyledProperty<bool> ServerOnLeftProperty = AvaloniaProperty.Register<FlowLine, bool>(nameof(ServerOnLeft));
    public static readonly StyledProperty<double> PhaseProperty = AvaloniaProperty.Register<FlowLine, double>(nameof(Phase));
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<FlowLine, IBrush?>(nameof(LineBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> UpBrushProperty = AvaloniaProperty.Register<FlowLine, IBrush?>(nameof(UpBrush), Brushes.Green);
    public static readonly StyledProperty<IBrush?> DownBrushProperty = AvaloniaProperty.Register<FlowLine, IBrush?>(nameof(DownBrush), Brushes.Blue);

    static FlowLine()
    {
        AffectsRender<FlowLine>(FlowProperty, LinkedProperty, ServerOnLeftProperty, PhaseProperty, LineBrushProperty, UpBrushProperty, DownBrushProperty);
        FlowProperty.Changed.AddClassHandler<FlowLine>((line, _) => line.UpdateMotion());
        LinkedProperty.Changed.AddClassHandler<FlowLine>((line, _) => line.UpdateMotion());
    }

    public LinkFlow Flow
    {
        get => GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    public bool Linked
    {
        get => GetValue(LinkedProperty);
        set => SetValue(LinkedProperty, value);
    }

    /// <summary>True for the line on the right of the server (its "up" runs right to left).</summary>
    public bool ServerOnLeft
    {
        get => GetValue(ServerOnLeftProperty);
        set => SetValue(ServerOnLeftProperty, value);
    }

    public double Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? UpBrush
    {
        get => GetValue(UpBrushProperty);
        set => SetValue(UpBrushProperty, value);
    }

    public IBrush? DownBrush
    {
        get => GetValue(DownBrushProperty);
        set => SetValue(DownBrushProperty, value);
    }

    /// <summary>True while dots are travelling (for tests).</summary>
    internal bool IsFlowing => Classes.Contains("flowing");

    private void UpdateMotion() => Classes.Set("flowing", Linked && Flow != LinkFlow.None);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var y = Bounds.Height / 2;
        if (w <= 4)
            return;
        var pen = Linked
            ? new Pen(LineBrush, 2, lineCap: PenLineCap.Round)
            : new Pen(LineBrush, 2, new DashStyle([2, 3], 0), PenLineCap.Round);
        context.DrawLine(pen, new Point(0, y), new Point(w, y));
        if (!Linked || Flow == LinkFlow.None)
            return;

        var both = Flow == LinkFlow.Both;
        if (Flow is LinkFlow.Up or LinkFlow.Both)
            DrawDots(context, w, both ? y - 3.5 : y, towardsRight: !ServerOnLeft, UpBrush);
        if (Flow is LinkFlow.Down or LinkFlow.Both)
            DrawDots(context, w, both ? y + 3.5 : y, towardsRight: ServerOnLeft, DownBrush);
    }

    private void DrawDots(DrawingContext context, double width, double y, bool towardsRight, IBrush? brush)
    {
        if (brush is null)
            return;
        var count = Math.Max(2, (int)(width / 34));
        var spacing = width / count;
        for (var i = 0; i < count; i++)
        {
            var t = (i + Phase) * spacing % width;
            var x = towardsRight ? t : width - t;
            var edge = Math.Min(x, width - x);
            var opacity = Math.Clamp(edge / 18, 0, 1); // fade in and out at the ends
            using (context.PushOpacity(opacity))
                context.DrawEllipse(brush, null, new Point(x, y), 3.2, 3.2);
        }
    }
}
