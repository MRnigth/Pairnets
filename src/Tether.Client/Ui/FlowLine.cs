using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Tether.Core.Client;

namespace Tether.Client.Ui;

/// <summary>
/// The line between a computer and the server (same as the Mac/Linux app). Solid while connected,
/// dashed when not, and dots travel along it while files move: green towards the server, blue
/// away from it. Stays still when Windows is set to show fewer animations.
/// </summary>
public sealed class FlowLine : FrameworkElement
{
    public static readonly DependencyProperty FlowProperty = DependencyProperty.Register(
        nameof(Flow), typeof(LinkFlow), typeof(FlowLine), new FrameworkPropertyMetadata(LinkFlow.None, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));

    public static readonly DependencyProperty LinkedProperty = DependencyProperty.Register(
        nameof(Linked), typeof(bool), typeof(FlowLine), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));

    public static readonly DependencyProperty ServerOnLeftProperty = DependencyProperty.Register(
        nameof(ServerOnLeft), typeof(bool), typeof(FlowLine), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(FlowLine), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(FlowLine), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public LinkFlow Flow
    {
        get => (LinkFlow)GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    public bool Linked
    {
        get => (bool)GetValue(LinkedProperty);
        set => SetValue(LinkedProperty, value);
    }

    /// <summary>True for the line on the right of the server (its "up" runs right to left).</summary>
    public bool ServerOnLeft
    {
        get => (bool)GetValue(ServerOnLeftProperty);
        set => SetValue(ServerOnLeftProperty, value);
    }

    public double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>True while dots are travelling (for the screenshot tool and checks).</summary>
    public bool IsFlowing { get; private set; }

    public FlowLine()
    {
        SetResourceReference(LineBrushProperty, "T.Line");
        IsVisibleChanged += (_, _) => UpdateMotion(); // nothing moves while the window is hidden
    }

    private static void OnMotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FlowLine)d).UpdateMotion();

    private void UpdateMotion()
    {
        var flowing = Linked && Flow != LinkFlow.None && IsVisible && SystemParameters.ClientAreaAnimation;
        if (flowing == IsFlowing)
            return;
        IsFlowing = flowing;
        BeginAnimation(PhaseProperty, flowing
            ? new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var y = ActualHeight / 2;
        if (w <= 4)
            return;
        var pen = new Pen(LineBrush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (!Linked)
            pen.DashStyle = new DashStyle([1, 1.5], 0);
        dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        if (!Linked || Flow == LinkFlow.None)
            return;
        var both = Flow == LinkFlow.Both;
        if (Flow is LinkFlow.Up or LinkFlow.Both)
            DrawDots(dc, w, both ? y - 3.5 : y, towardsRight: !ServerOnLeft, Visuals.Resource<Brush>("S.Green"));
        if (Flow is LinkFlow.Down or LinkFlow.Both)
            DrawDots(dc, w, both ? y + 3.5 : y, towardsRight: ServerOnLeft, Visuals.Resource<Brush>("S.Blue"));
    }

    private void DrawDots(DrawingContext dc, double width, double y, bool towardsRight, Brush? brush)
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
            dc.PushOpacity(Math.Clamp(edge / 18, 0, 1)); // fade in and out at the ends
            dc.DrawEllipse(brush, null, new Point(x, y), 3.2, 3.2);
            dc.Pop();
        }
    }
}
