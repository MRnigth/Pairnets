using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The Developer beta look's two-layer progress ring (drawn in a 132-unit box: stroke 12, radius 48, round caps, from
/// 12 o'clock clockwise). Layer 1 (strong) is the files done, layer 2 (lighter) reaches on from it; the centre shows the
/// count, or three waving dots while there is none yet. While a pass runs a thin arc circles outside the ring. All motion
/// comes from one frame loop that runs only while something moves and <see cref="Motion.Live"/> is true; with animations
/// off nothing moves.
/// </summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<ProgressRingModel?> ModelProperty = AvaloniaProperty.Register<ProgressRing, ProgressRingModel?>(nameof(Model));

    private const double Box = 132;
    private const double Stroke = 12;
    private const double Radius = 48;
    private const double OuterRadius = 62;
    private const double SpinSeconds = 1.3;
    private const double TweenSeconds = 0.45;
    private const double Layer2Delay = 0.12;
    private const double ColourSeconds = 0.3;
    private const double FadeSeconds = 0.3;

    static ProgressRing()
    {
        ModelProperty.Changed.AddClassHandler<ProgressRing>((ring, _) => ring.OnModelChanged());
        AffectsRender<ProgressRing>(ModelProperty);
    }

    public ProgressRing()
    {
        Width = 104;
        Height = 104;
        Focusable = false;
        ActualThemeVariantChanged += OnThemeChanged;
    }

    public ProgressRingModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    // Shown values and the tweens towards the targets.
    private double _done;
    private double _reach;
    private double _doneFrom;
    private double _reachFrom;
    private TimeSpan? _tweenStart;
    private bool _tweening;
    private bool _first = true;

    private Color _c1;
    private Color _c2;
    private Color _c1From;
    private Color _c2From;
    private Color _c1To;
    private Color _c2To;
    private TimeSpan? _colourStart;
    private bool _colouring;
    private RingTone _toneSeen = RingTone.Quiet;
    private bool _colourReady;

    private double _spinAlpha;
    private double _spinAngle;
    private double _clock; // seconds since the first frame; drives the dots
    private TimeSpan? _lastFrame;
    private bool _frameRequested;

    /// <summary>True while the frame loop is asked for (tests).</summary>
    internal bool FramesRequested => _frameRequested;

    /// <summary>True while the circling arc exists (fading in, turning or fading out).</summary>
    internal bool SpinnerPresent => _spinAlpha > 0.001;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window window)
            Motion.Track(window);
        Motion.LiveChanged += OnLiveChanged;
        ResolveColours(snap: !_colourReady);
        UpdateMotion();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Motion.LiveChanged -= OnLiveChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ResolveColours(snap: true);
        InvalidateVisual();
    }

    private void OnLiveChanged() => UpdateMotion();

    private void OnModelChanged()
    {
        var m = Model;
        if (m is null)
        {
            InvalidateVisual();
            return;
        }
        AutomationProperties.SetName(this, m.Description);
        var animate = !_first && Motion.Enabled && Motion.Live && IsEffectivelyVisible;
        if (_first || !animate)
        {
            _done = m.Done;
            _reach = m.Reach;
            _tweening = false;
        }
        else if (Math.Abs(m.Done - _done) > 1e-9 || Math.Abs(m.Reach - _reach) > 1e-9)
        {
            _doneFrom = _done;
            _reachFrom = _reach;
            _tweenStart = null;
            _tweening = true;
        }
        _first = false;
        _targetDone = m.Done;
        _targetReach = m.Reach;
        if (m.Tone != _toneSeen || !_colourReady)
        {
            _toneSeen = m.Tone;
            ResolveColours(snap: !animate || !_colourReady);
        }
        UpdateMotion();
        InvalidateVisual();
    }

    private double _targetDone;
    private double _targetReach;

    private void ResolveColours(bool snap)
    {
        var tone = Model?.Tone ?? RingTone.Quiet;
        var (k1, k2) = tone switch
        {
            RingTone.Accent => ("T.RingAccent", "T.RingAccent2"),
            RingTone.Ok => ("T.RingOk", "T.RingOk2"),
            RingTone.Warn => ("T.RingWarn", "T.RingWarn2"),
            RingTone.Bad => ("T.RingBad", "T.RingBad2"),
            _ => ("T.Off", "T.Track"),
        };
        var c1 = Lookup(k1, Colors.Gray);
        var c2 = Lookup(k2, Colors.LightGray);
        if (snap)
        {
            _c1 = _c1To = _c1From = c1;
            _c2 = _c2To = _c2From = c2;
            _colouring = false;
        }
        else
        {
            _c1From = _c1;
            _c2From = _c2;
            _c1To = c1;
            _c2To = c2;
            _colourStart = null;
            _colouring = true;
        }
        _colourReady = true;
    }

    private Color Lookup(string key, Color fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is ISolidColorBrush brush)
            return brush.Color;
        return fallback;
    }

    private bool Wants
    {
        get
        {
            if (!Motion.Enabled || !Motion.Live || !IsEffectivelyVisible)
                return false;
            var m = Model;
            return _tweening || _colouring || m is { Thinking: true } || m is { Syncing: true } || _spinAlpha > 0.001;
        }
    }

    private void UpdateMotion()
    {
        if (!Motion.Enabled || !Motion.Live)
        {
            // Nothing moves: land every tween and drop the circling arc.
            if (Model is { } m)
            {
                _done = m.Done;
                _reach = m.Reach;
            }
            _tweening = false;
            if (_colouring)
            {
                _c1 = _c1To;
                _c2 = _c2To;
                _colouring = false;
            }
            _spinAlpha = 0;
            InvalidateVisual();
            return;
        }
        if (Wants && !_frameRequested && TopLevel.GetTopLevel(this) is { } top)
        {
            _frameRequested = true;
            _lastFrame = null;
            top.RequestAnimationFrame(OnFrame);
        }
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;
        if (!Wants || TopLevel.GetTopLevel(this) is not { } top)
        {
            if (Model is { } m && !(Motion.Enabled && Motion.Live))
                UpdateMotion();
            return; // idle: no more frames until something changes (model, Motion.LiveChanged)
        }
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrame = now;
        _clock += dt;

        if (_tweening)
        {
            _tweenStart ??= now;
            var t = (now - _tweenStart.Value).TotalSeconds;
            var t1 = Ease(t / TweenSeconds);
            var t2 = Ease((t - Layer2Delay) / TweenSeconds);
            _done = _doneFrom + (_targetDone - _doneFrom) * t1;
            _reach = _reachFrom + (_targetReach - _reachFrom) * t2;
            if (t >= TweenSeconds + Layer2Delay)
            {
                _done = _targetDone;
                _reach = _targetReach;
                _tweening = false;
            }
        }
        if (_colouring)
        {
            _colourStart ??= now;
            var t = Math.Clamp((now - _colourStart.Value).TotalSeconds / ColourSeconds, 0, 1);
            _c1 = Mix(_c1From, _c1To, t);
            _c2 = Mix(_c2From, _c2To, t);
            if (t >= 1)
                _colouring = false;
        }
        var syncing = Model is { Syncing: true };
        _spinAlpha = Math.Clamp(_spinAlpha + (syncing ? dt : -dt) / FadeSeconds, 0, 1);
        if (_spinAlpha > 0)
            _spinAngle = (_spinAngle + dt / SpinSeconds * 360) % 360;

        InvalidateVisual();
        if (Wants)
        {
            _frameRequested = true;
            top.RequestAnimationFrame(OnFrame);
        }
    }

    private static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext context)
    {
        var m = Model;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (m is null || size <= 0)
            return;
        if (!_colourReady)
            ResolveColours(snap: true);

        var scale = size / Box;
        var origin = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
        {
            var centre = new Point(Box / 2, Box / 2);
            var track = Brush("T.Track", Colors.LightGray);
            context.DrawEllipse(null, new Pen(track, Stroke), centre, Radius, Radius);

            // Layer 2 first, layer 1 on top so its round end overlaps the lighter arc.
            DrawArc(context, centre, _reach, new SolidColorBrush(_c2));
            DrawArc(context, centre, _done, new SolidColorBrush(_c1));

            if (m.Syncing || _spinAlpha > 0.001)
                DrawSpinner(context, centre);

            DrawCentre(context, m, scale);
        }
    }

    private static void DrawArc(DrawingContext context, Point centre, double value, IBrush brush)
    {
        if (value <= 0.0005)
            return; // a round cap alone would show a dot
        var pen = new Pen(brush, Stroke, lineCap: PenLineCap.Round);
        if (value >= 0.9995)
        {
            context.DrawEllipse(null, pen, centre, Radius, Radius);
            return;
        }
        context.DrawGeometry(null, pen, Arc(centre, Radius, -90, value * 360));
    }

    private void DrawSpinner(DrawingContext context, Point centre)
    {
        var accent = Lookup("T.RingAccent", Colors.CornflowerBlue);
        if (!Motion.Enabled || !Motion.Live)
        {
            // Animations off (or nobody looking): a faint static ring where the arc would run.
            var faint = new Pen(new SolidColorBrush(accent, 0.22), 3);
            if (Model is { Syncing: true })
                context.DrawEllipse(null, faint, centre, OuterRadius, OuterRadius);
            return;
        }
        var pen = new Pen(new SolidColorBrush(accent, _spinAlpha), 3, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, pen, Arc(centre, OuterRadius, -90 + _spinAngle, 0.22 * 360));
    }

    private static StreamGeometry Arc(Point centre, double radius, double startDeg, double sweepDeg)
    {
        Point At(double deg) => new(centre.X + radius * Math.Cos(deg * Math.PI / 180), centre.Y + radius * Math.Sin(deg * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(startDeg), false);
            g.ArcTo(At(startDeg + sweepDeg), new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        return geometry;
    }

    private IBrush Brush(string key, Color fallback) => new SolidColorBrush(Lookup(key, fallback));

    private void DrawCentre(DrawingContext context, ProgressRingModel m, double scale)
    {
        var centre = new Point(Box / 2, Box / 2);
        if (m.Thinking)
        {
            var ink = Lookup("T.Text", Colors.Gray);
            for (var i = 0; i < 3; i++)
            {
                // One round per second, each dot a third of a round behind; from ~6 below the line to ~8 above.
                var phase = Motion.Enabled && Motion.Live ? _clock - i / 3.0 : 0.25 * 0 + 0.5;
                var wave = (1 + Math.Sin(2 * Math.PI * phase)) / 2;
                var y = centre.Y + 6 - wave * 14;
                var alpha = 0.45 + 0.55 * wave;
                context.DrawEllipse(new SolidColorBrush(ink, alpha), null, new Point(centre.X + (i - 1) * 14, y), 4, 4);
            }
            return;
        }
        var text = Lookup("T.Text", Colors.Black);
        var muted = Lookup("T.Muted", Colors.Gray);
        var family = Visuals.DisplayFamily();
        var sans = Visuals.SansFamily();
        var bigSize = m.Big.Length <= 5 ? 31.0 : 24.0; // 25 px on the card
        var big = Format(m.Big, family, FontWeight.Bold, bigSize, text);
        var small = m.Small.Length > 0 ? Format(m.Small, sans, FontWeight.Medium, 13, muted) : null;
        var total = big.Height + (small is null ? 0 : small.Height + 1);
        var top = centre.Y - total / 2;
        context.DrawText(big, new Point(centre.X - big.Width / 2, top));
        if (small is not null)
            context.DrawText(small, new Point(centre.X - small.Width / 2, top + big.Height + 1));
    }

    private static FormattedText Format(string text, FontFamily family, FontWeight weight, double size, Color colour) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, new SolidColorBrush(colour));
}
