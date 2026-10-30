using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Developer beta look's two-layer progress ring (Overview heading card): a pale track, a lighter arc from 12 o'clock
/// to <see cref="ProgressRingModel.Reach"/> and, drawn on top, a strong arc to <see cref="ProgressRingModel.Done"/>, so the
/// strong arc's round end overlaps the lighter one. The centre says the count (or three waving dots while no count exists).
/// While a pass runs a thin accent arc circles just outside the ring; it lives in the visual tree only then, so a finished
/// ring costs no CPU. Design units are a 132 box with stroke 12 and radius 48, drawn here at 104 x 104.
/// </summary>
public sealed class ProgressRing : Grid
{
    /// <summary>The size on the card.</summary>
    public const double Size = 104;

    private const double OrbitRadius = 45;
    private const double OrbitThickness = 3;
    private const double OrbitShare = 0.22;
    private static readonly TimeSpan MoveTime = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan LayerTwoDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ColourTime = TimeSpan.FromMilliseconds(300);

    private readonly RingArcs _arcs = new();
    private readonly TextBlock _big = new();
    private readonly TextBlock _small = new();
    private readonly StackPanel _numbers = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Height = 28, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Ellipse[] _dot = new Ellipse[3];
    private Path? _orbit;
    private ProgressRingModel? _model;
    private bool _wantSyncing;
    private bool _wantThinking;
    private int _orbitGeneration;
    private (Color, Color) _target;

    public ProgressRing()
    {
        Width = Size;
        Height = Size;
        VerticalAlignment = VerticalAlignment.Center;
        // The orbit sits a little outside the ring's own box and must not be clipped.
        ClipToBounds = false;
        Children.Add(_arcs);

        _big.FontFamily = (FontFamily)Application.Current.FindResource("F.Display");
        _big.FontSize = 25;
        _big.FontWeight = FontWeights.Bold;
        _big.HorizontalAlignment = HorizontalAlignment.Center;
        _big.SetResourceReference(TextBlock.ForegroundProperty, "T.Text");
        _small.FontSize = 10.5;
        _small.Margin = new Thickness(0, 3, 0, 0);
        _small.HorizontalAlignment = HorizontalAlignment.Center;
        _small.TextAlignment = TextAlignment.Center;
        _small.TextWrapping = TextWrapping.Wrap;
        _small.MaxWidth = 70;
        _small.SetResourceReference(TextBlock.ForegroundProperty, "T.Muted");
        _numbers.Children.Add(_big);
        _numbers.Children.Add(_small);
        Children.Add(_numbers);

        for (var i = 0; i < _dot.Length; i++)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0), Opacity = 0.7, RenderTransform = new TranslateTransform() };
            dot.SetResourceReference(Shape.FillProperty, "T.Muted");
            _dot[i] = dot;
            _dots.Children.Add(dot);
        }

        IsVisibleChanged += (_, _) => SyncMotion();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            SyncMotion();
        };
    }

    // ------------------------------------------------------------------ what the tests read

    /// <summary>The strong layer's end now (0-1), animation included.</summary>
    internal double DoneShown => _arcs.Done;

    /// <summary>The lighter layer's end now (0-1), animation included.</summary>
    internal double ReachShown => _arcs.Reach;

    internal string BigText => _big.Text;

    internal string SmallText => _small.Text;

    /// <summary>The thin circling arc is in the visual tree (so a clock runs).</summary>
    internal bool OrbitAttached => _orbit is not null && Children.Contains(_orbit);

    /// <summary>The three waving dots are shown.</summary>
    internal bool DotsAttached => _dots.Parent is not null;

    internal RingTone Tone => _model?.Tone ?? RingTone.Accent;

    private static bool Animated => SystemParameters.ClientAreaAnimation;

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.BeginInvoke(SyncMotion);
    }

    // ------------------------------------------------------------------ showing a model

    /// <summary>Moves the ring to a new state: values glide from where they are, the colour fades.</summary>
    public void Apply(ProgressRingModel m)
    {
        var first = _model is null;
        var before = _model;
        _model = m;
        AutomationProperties.SetName(this, m.Description);

        if (first || before!.Done != m.Done)
            Move(RingArcs.DoneProperty, m.Done, TimeSpan.Zero, first);
        if (first || before!.Reach != m.Reach)
            Move(RingArcs.ReachProperty, m.Reach, LayerTwoDelay, first);

        var (c1, c2) = Colours(m.Tone);
        if (first || _target != (c1, c2))
        {
            Fade(RingArcs.C1Property, _arcs.C1, c1, first);
            Fade(RingArcs.C2Property, _arcs.C2, c2, first);
            _target = (c1, c2);
        }

        _big.Text = m.Thinking ? string.Empty : m.Big;
        _small.Text = m.Thinking ? string.Empty : m.Small;
        _small.Visibility = _small.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _numbers.Visibility = m.Thinking ? Visibility.Collapsed : Visibility.Visible;

        _wantThinking = m.Thinking;
        _wantSyncing = m.Syncing;
        SyncMotion();
    }

    /// <summary>Stops every clock (the window sits in the tray); <see cref="Apply"/> or becoming visible starts them again.</summary>
    public void StopMotion()
    {
        StopDots();
        DetachOrbit(fade: false);
    }

    private void Move(DependencyProperty property, double target, TimeSpan delay, bool snap)
    {
        _arcs.BeginAnimation(property, null);
        if (snap || !Animated || !IsLoaded)
        {
            _arcs.SetValue(property, target);
            return;
        }
        // To only: it starts from wherever the arc is now (never from 0 after the first show).
        _arcs.BeginAnimation(property, new DoubleAnimation(target, MoveTime)
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private void Fade(DependencyProperty property, Color from, Color to, bool snap)
    {
        _arcs.BeginAnimation(property, null);
        if (snap || !Animated || !IsLoaded)
        {
            _arcs.SetValue(property, to);
            return;
        }
        _arcs.SetValue(property, from);
        _arcs.BeginAnimation(property, new ColorAnimation(to, ColourTime));
    }

    private (Color, Color) Colours(RingTone tone)
    {
        var (k1, k2) = tone switch
        {
            RingTone.Ok => ("T.RingOk", "T.RingOk2"),
            RingTone.Warn => ("T.RingWarn", "T.RingWarn2"),
            RingTone.Bad => ("T.RingBad", "T.RingBad2"),
            RingTone.Quiet => ("T.Muted", "T.SubtleHover"),
            _ => ("T.RingAccent", "T.RingAccent2"),
        };
        return (Resolve(k1), Resolve(k2));
    }

    private static Color Resolve(string key) => Application.Current.FindResource(key) is SolidColorBrush b ? b.Color : Colors.Gray;

    // ------------------------------------------------------------------ motion that exists only while it is needed

    private void SyncMotion()
    {
        var live = IsVisible && IsLoaded;
        if (live && _wantThinking)
            ShowDots();
        else
            HideDots();

        if (live && _wantSyncing)
            AttachOrbit();
        else
            DetachOrbit(fade: live && Animated);
    }

    private void ShowDots()
    {
        if (_dots.Parent is null)
            Children.Add(_dots);
        for (var i = 0; i < _dot.Length; i++)
        {
            var move = (TranslateTransform)_dot[i].RenderTransform;
            if (!Animated)
            {
                move.BeginAnimation(TranslateTransform.YProperty, null);
                _dot[i].BeginAnimation(OpacityProperty, null);
                _dot[i].Opacity = 0.7;
                move.Y = 0;
                continue;
            }
            // One round per second, each dot a third of a round behind the one before; never pausing.
            var begin = TimeSpan.FromSeconds(-0.33 * i);
            move.BeginAnimation(TranslateTransform.YProperty, Wave(6, -8, begin));
            _dot[i].BeginAnimation(OpacityProperty, Wave(0.45, 1, begin));
        }
    }

    private static DoubleAnimation Wave(double low, double high, TimeSpan begin) => new(low, high, TimeSpan.FromSeconds(0.5))
    {
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        BeginTime = begin,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };

    private void HideDots()
    {
        StopDots();
        if (_dots.Parent is not null)
            Children.Remove(_dots);
    }

    private void StopDots()
    {
        foreach (var dot in _dot)
        {
            ((TranslateTransform)dot.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
            dot.BeginAnimation(OpacityProperty, null);
        }
    }

    private void AttachOrbit()
    {
        _orbitGeneration++;
        if (_orbit is not null && Children.Contains(_orbit))
        {
            _orbit.BeginAnimation(OpacityProperty, null); // a fade-out that was under way is cancelled
            if (Animated)
                _orbit.Opacity = 1;
            return;
        }
        var path = new Path
        {
            Width = Size,
            Height = Size,
            IsHitTestVisible = false,
            StrokeThickness = OrbitThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Animated ? OrbitArc() : new EllipseGeometry(new Point(Size / 2, Size / 2), OrbitRadius, OrbitRadius),
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        path.SetResourceReference(Shape.StrokeProperty, "T.RingAccent");
        _orbit = path;
        Children.Add(path);
        if (!Animated)
        {
            path.Opacity = 0.35; // a faint static ring: nothing moves
            return;
        }
        path.RenderTransform = new RotateTransform();
        path.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever });
        path.Opacity = 0;
        path.BeginAnimation(OpacityProperty, new DoubleAnimation(1, ColourTime));
    }

    private void DetachOrbit(bool fade)
    {
        if (_orbit is not { } path || !Children.Contains(path))
            return;
        var generation = ++_orbitGeneration;
        void Remove()
        {
            if (generation != _orbitGeneration)
                return; // syncing started again meanwhile
            // With Windows animations off the orbit keeps the frozen default transform, which can't be animated.
            (path.RenderTransform as RotateTransform)?.BeginAnimation(RotateTransform.AngleProperty, null);
            path.BeginAnimation(OpacityProperty, null);
            Children.Remove(path);
        }
        if (!fade)
        {
            Remove();
            return;
        }
        var fadeOut = new DoubleAnimation(0, ColourTime);
        fadeOut.Completed += (_, _) => Remove();
        path.BeginAnimation(OpacityProperty, fadeOut);
    }

    private static Geometry OrbitArc()
    {
        const double centre = Size / 2;
        var end = OrbitShare * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(centre, centre - OrbitRadius), false, false);
            c.ArcTo(new Point(centre + OrbitRadius * Math.Sin(end), centre - OrbitRadius * Math.Cos(end)),
                new Size(OrbitRadius, OrbitRadius), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // ------------------------------------------------------------------ the two arcs

    /// <summary>Track, lighter layer and strong layer, each value and colour animatable.</summary>
    private sealed class RingArcs : FrameworkElement
    {
        public static readonly DependencyProperty DoneProperty = Reg(nameof(Done), typeof(double), 0.0);
        public static readonly DependencyProperty ReachProperty = Reg(nameof(Reach), typeof(double), 0.0);
        public static readonly DependencyProperty C1Property = Reg(nameof(C1), typeof(Color), Colors.Gray);
        public static readonly DependencyProperty C2Property = Reg(nameof(C2), typeof(Color), Colors.Gray);
        public static readonly DependencyProperty TrackProperty = Reg(nameof(Track), typeof(Brush), null);

        public RingArcs()
        {
            IsHitTestVisible = false;
            this.SetResourceReference(TrackProperty, "T.Track");
        }

        public double Done => (double)GetValue(DoneProperty);

        public double Reach => (double)GetValue(ReachProperty);

        public Color C1 => (Color)GetValue(C1Property);

        public Color C2 => (Color)GetValue(C2Property);

        public Brush? Track => (Brush?)GetValue(TrackProperty);

        private static DependencyProperty Reg(string name, Type type, object? value) => DependencyProperty.Register(name, type, typeof(RingArcs),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

        protected override void OnRender(DrawingContext dc)
        {
            var size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0)
                return;
            var k = size / 132.0;
            var radius = 48 * k;
            var centre = new Point(size / 2, size / 2);
            Pen Round(Brush brush) => new(brush, 12 * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

            dc.DrawEllipse(null, new Pen(Track ?? Brushes.Gray, 12 * k), centre, radius, radius);
            // Lighter layer first, strong layer on top: its round end overlaps the lighter one.
            Arc(dc, Round(new SolidColorBrush(C2)), centre, radius, Math.Max(Reach, Done));
            Arc(dc, Round(new SolidColorBrush(C1)), centre, radius, Done);
        }

        private static void Arc(DrawingContext dc, Pen pen, Point centre, double radius, double share)
        {
            // A zero-length arc is skipped: a round cap alone would show a dot.
            if (share < 0.002)
                return;
            share = Math.Min(share, 1);
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
            {
                var top = new Point(centre.X, centre.Y - radius);
                c.BeginFigure(top, false, false);
                if (share >= 0.999)
                {
                    var bottom = new Point(centre.X, centre.Y + radius);
                    c.ArcTo(bottom, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                    c.ArcTo(top, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                }
                else
                {
                    var angle = share * 2 * Math.PI;
                    var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
                    c.ArcTo(end, new Size(radius, radius), 0, share > 0.5, SweepDirection.Clockwise, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
