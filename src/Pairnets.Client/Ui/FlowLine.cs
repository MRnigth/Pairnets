using System.Windows;
using System.Windows.Media;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The line between a computer and the server (same as the Mac/Linux app). Solid while connected,
/// dashed when not, and dots travel along it while files move: green towards the server, blue
/// away from it. The dots go as fast as the bytes do (<see cref="UpSpeed"/>, <see cref="DownSpeed"/>,
/// from <see cref="DeviceMap.DotSpeed"/>), slow down with them, and stop and fade when the transfer
/// is <see cref="Stalled"/>. Stays still when Windows is set to show fewer animations.
/// </summary>
public sealed class FlowLine : FrameworkElement
{
    public static readonly DependencyProperty FlowProperty = DependencyProperty.Register(
        nameof(Flow), typeof(LinkFlow), typeof(FlowLine), new FrameworkPropertyMetadata(LinkFlow.None, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));

    public static readonly DependencyProperty LinkedProperty = DependencyProperty.Register(
        nameof(Linked), typeof(bool), typeof(FlowLine), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnMotionChanged));

    public static readonly DependencyProperty ServerOnLeftProperty = DependencyProperty.Register(
        nameof(ServerOnLeft), typeof(bool), typeof(FlowLine), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UpSpeedProperty = DependencyProperty.Register(
        nameof(UpSpeed), typeof(double), typeof(FlowLine), new FrameworkPropertyMetadata(DeviceMap.DefaultDotSpeed));

    public static readonly DependencyProperty DownSpeedProperty = DependencyProperty.Register(
        nameof(DownSpeed), typeof(double), typeof(FlowLine), new FrameworkPropertyMetadata(DeviceMap.DefaultDotSpeed));

    public static readonly DependencyProperty StalledProperty = DependencyProperty.Register(
        nameof(Stalled), typeof(bool), typeof(FlowLine), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(FlowLine), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Dots this far apart, in pixels.</summary>
    private const double Spacing = 34;

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

    /// <summary>How fast the "up" dots travel, in pixels per second (0: they stand still).</summary>
    public double UpSpeed
    {
        get => (double)GetValue(UpSpeedProperty);
        set => SetValue(UpSpeedProperty, value);
    }

    /// <summary>How fast the "down" dots travel, in pixels per second.</summary>
    public double DownSpeed
    {
        get => (double)GetValue(DownSpeedProperty);
        set => SetValue(DownSpeedProperty, value);
    }

    /// <summary>Files should move but nothing has for a while: the dots stand still and fade.</summary>
    public bool Stalled
    {
        get => (bool)GetValue(StalledProperty);
        set => SetValue(StalledProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>True while dots are travelling (for the screenshot tool and checks).</summary>
    public bool IsFlowing { get; private set; }

    // Where each row of dots is (pixels along the line) and how fast it goes now; the speed eases towards the
    // target, so a change in MB/s speeds the dots up or slows them down instead of making them jump.
    private double _upPhase;
    private double _downPhase = Spacing / 2;
    private double _upVelocity;
    private double _downVelocity;
    private double _dim = 1;
    private TimeSpan? _lastFrame;

    public FlowLine()
    {
        SetResourceReference(LineBrushProperty, "T.Line");
        IsVisibleChanged += (_, _) => UpdateMotion(); // nothing moves while the window is hidden
        Unloaded += (_, _) => Stop();
        Loaded += (_, _) => UpdateMotion();
    }

    private static void OnMotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FlowLine)d).UpdateMotion();

    private void UpdateMotion()
    {
        var flowing = Linked && Flow != LinkFlow.None && IsVisible && SystemParameters.ClientAreaAnimation;
        if (flowing == IsFlowing)
            return;
        if (flowing)
        {
            IsFlowing = true;
            _lastFrame = null;
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            Stop();
        }
    }

    private void Stop()
    {
        if (!IsFlowing)
            return;
        IsFlowing = false;
        CompositionTarget.Rendering -= OnFrame;
        _upVelocity = _downVelocity = 0;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (now == _lastFrame)
            return; // Rendering can fire more than once per frame
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrame = now;
        var ease = Math.Min(1, dt * 2.6);
        _upVelocity += ((Stalled ? 0 : UpSpeed) - _upVelocity) * ease;
        _downVelocity += ((Stalled ? 0 : DownSpeed) - _downVelocity) * ease;
        var period = Math.Max(1, ActualWidth); // the dots look the same one line-length on
        _upPhase = (_upPhase + _upVelocity * dt) % period;
        _downPhase = (_downPhase + _downVelocity * dt) % period;
        _dim += ((Stalled ? 0.35 : 1) - _dim) * Math.Min(1, dt * 3);
        InvalidateVisual();
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
        var dim = IsFlowing ? _dim : Stalled ? 0.35 : 1;
        if (Flow is LinkFlow.Up or LinkFlow.Both)
            DrawDots(dc, w, both ? y - 3.5 : y, _upPhase, towardsRight: !ServerOnLeft, Visuals.Resource<Brush>("S.Green"), dim);
        if (Flow is LinkFlow.Down or LinkFlow.Both)
            DrawDots(dc, w, both ? y + 3.5 : y, _downPhase, towardsRight: ServerOnLeft, Visuals.Resource<Brush>("S.Blue"), dim);
    }

    private static void DrawDots(DrawingContext dc, double width, double y, double phase, bool towardsRight, Brush? brush, double dim)
    {
        if (brush is null)
            return;
        var count = Math.Max(2, (int)(width / Spacing));
        var spacing = width / count;
        for (var i = 0; i < count; i++)
        {
            var t = (i * spacing + phase) % width;
            var x = towardsRight ? t : width - t;
            var edge = Math.Min(x, width - x);
            dc.PushOpacity(Math.Clamp(edge / 18, 0, 1) * dim); // fade in and out at the ends
            dc.DrawEllipse(brush, null, new Point(x, y), 3.2, 3.2);
            dc.Pop();
        }
    }
}
