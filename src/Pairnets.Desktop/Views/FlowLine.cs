using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The line between a computer and the server (same as the Windows app). Solid while connected, dashed when not,
/// and dots travel along it while files move: green towards the server, blue away from it. The dots go as fast
/// as the bytes do (<see cref="UpSpeed"/>, <see cref="DownSpeed"/>, from <see cref="DeviceMap.DotSpeed"/>), slow
/// down with them, and stop and fade when the transfer is <see cref="Stalled"/>. They move one frame at a time
/// while the line has the "flowing" class.
/// </summary>
public sealed class FlowLine : Control
{
    public static readonly StyledProperty<LinkFlow> FlowProperty = AvaloniaProperty.Register<FlowLine, LinkFlow>(nameof(Flow));
    public static readonly StyledProperty<bool> LinkedProperty = AvaloniaProperty.Register<FlowLine, bool>(nameof(Linked));
    public static readonly StyledProperty<bool> ServerOnLeftProperty = AvaloniaProperty.Register<FlowLine, bool>(nameof(ServerOnLeft));
    public static readonly StyledProperty<double> UpSpeedProperty = AvaloniaProperty.Register<FlowLine, double>(nameof(UpSpeed), DeviceMap.DefaultDotSpeed);
    public static readonly StyledProperty<double> DownSpeedProperty = AvaloniaProperty.Register<FlowLine, double>(nameof(DownSpeed), DeviceMap.DefaultDotSpeed);
    public static readonly StyledProperty<bool> StalledProperty = AvaloniaProperty.Register<FlowLine, bool>(nameof(Stalled));
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<FlowLine, IBrush?>(nameof(LineBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> UpBrushProperty = AvaloniaProperty.Register<FlowLine, IBrush?>(nameof(UpBrush), Brushes.Green);
    public static readonly StyledProperty<IBrush?> DownBrushProperty = AvaloniaProperty.Register<FlowLine, IBrush?>(nameof(DownBrush), Brushes.Blue);

    /// <summary>Dots this far apart, in pixels.</summary>
    private const double Spacing = 34;

    static FlowLine()
    {
        AffectsRender<FlowLine>(FlowProperty, LinkedProperty, ServerOnLeftProperty, StalledProperty, LineBrushProperty, UpBrushProperty, DownBrushProperty);
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

    /// <summary>How fast the "up" dots travel, in pixels per second (0: they stand still).</summary>
    public double UpSpeed
    {
        get => GetValue(UpSpeedProperty);
        set => SetValue(UpSpeedProperty, value);
    }

    /// <summary>How fast the "down" dots travel, in pixels per second.</summary>
    public double DownSpeed
    {
        get => GetValue(DownSpeedProperty);
        set => SetValue(DownSpeedProperty, value);
    }

    /// <summary>Files should move but nothing has for a while: the dots stand still and fade.</summary>
    public bool Stalled
    {
        get => GetValue(StalledProperty);
        set => SetValue(StalledProperty, value);
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

    // Where each row of dots is (pixels along the line) and how fast it goes now; the speed eases towards the
    // target, so a change in MB/s speeds the dots up or slows them down instead of making them jump.
    private double _upPhase;
    private double _downPhase = Spacing / 2;
    private double _upVelocity;
    private double _downVelocity;
    private double _dim = 1;
    private TimeSpan? _lastFrame;
    private bool _frameRequested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateMotion();
    }

    private void UpdateMotion()
    {
        Classes.Set("flowing", Linked && Flow != LinkFlow.None);
        if (IsFlowing && !_frameRequested && TopLevel.GetTopLevel(this) is { } top)
        {
            _frameRequested = true;
            _lastFrame = null;
            top.RequestAnimationFrame(OnFrame);
        }
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;
        if (!IsFlowing || TopLevel.GetTopLevel(this) is not { } top)
        {
            _upVelocity = _downVelocity = 0;
            return;
        }
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrame = now;
        var ease = Math.Min(1, dt * 2.6);
        _upVelocity += ((Stalled ? 0 : UpSpeed) - _upVelocity) * ease;
        _downVelocity += ((Stalled ? 0 : DownSpeed) - _downVelocity) * ease;
        var period = Math.Max(1, Bounds.Width); // the dots look the same one line-length on
        _upPhase = (_upPhase + _upVelocity * dt) % period;
        _downPhase = (_downPhase + _downVelocity * dt) % period;
        _dim += ((Stalled ? 0.35 : 1) - _dim) * Math.Min(1, dt * 3);
        InvalidateVisual();
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

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
            DrawDots(context, w, both ? y - 3.5 : y, _upPhase, towardsRight: !ServerOnLeft, UpBrush);
        if (Flow is LinkFlow.Down or LinkFlow.Both)
            DrawDots(context, w, both ? y + 3.5 : y, _downPhase, towardsRight: ServerOnLeft, DownBrush);
    }

    private void DrawDots(DrawingContext context, double width, double y, double phase, bool towardsRight, IBrush? brush)
    {
        if (brush is null)
            return;
        var count = Math.Max(2, (int)(width / Spacing));
        var spacing = width / count;
        var dim = _frameRequested ? _dim : Stalled ? 0.35 : 1;
        for (var i = 0; i < count; i++)
        {
            var t = (i * spacing + phase) % width;
            var x = towardsRight ? t : width - t;
            var edge = Math.Min(x, width - x);
            var opacity = Math.Clamp(edge / 18, 0, 1) * dim; // fade in and out at the ends
            using (context.PushOpacity(opacity))
                context.DrawEllipse(brush, null, new Point(x, y), 3.2, 3.2);
        }
    }
}
