using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pairnets.Client.Ui;

/// <summary>
/// Small, friendly animations (same as the Mac/Linux app): bob, spin, pulse, pop, travelling
/// arrows, rows sliding in and bars gliding to new values. Each looping animation is started
/// once and kept running until it is switched off, so the 4-times-a-second refresh does not
/// restart it. Everything stays still when Windows is set to show fewer animations.
/// </summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<UIElement, HashSet<string>> Running = new();

    private static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Scale, rotate and translate around the element's centre.</summary>
    private static (ScaleTransform Scale, RotateTransform Rotate, TranslateTransform Move) Transforms(UIElement e)
    {
        if (e.RenderTransform is TransformGroup { Children.Count: 3 } g
            && g.Children[0] is ScaleTransform s && g.Children[1] is RotateTransform r && g.Children[2] is TranslateTransform t)
            return (s, r, t);
        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var move = new TranslateTransform();
        e.RenderTransformOrigin = new Point(0.5, 0.5);
        e.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
        return (scale, rotate, move);
    }

    /// <summary>Starts or stops a looping animation; does nothing if it is already in that state.</summary>
    private static void Loop(UIElement e, string name, bool on, Action<UIElement> start, Action<UIElement> stop)
    {
        var set = Running.GetOrCreateValue(e);
        on &= Enabled;
        if (on == set.Contains(name))
            return;
        if (on)
        {
            set.Add(name);
            start(e);
        }
        else
        {
            set.Remove(name);
            stop(e);
        }
    }

    private static DoubleAnimation Wave(double from, double to, double seconds) => new(from, to, TimeSpan.FromSeconds(seconds / 2))
    {
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
    };

    /// <summary>Gentle up-and-down bob (the server icon waiting for an update, the update arrow).</summary>
    public static void Bob(UIElement e, bool on) => Loop(e, "bob", on,
        x => Transforms(x).Move.BeginAnimation(TranslateTransform.YProperty, Wave(0, -6, 1.6)),
        x => Transforms(x).Move.BeginAnimation(TranslateTransform.YProperty, null));

    /// <summary>
    /// Continuous rotation (the ring while the server updates). <paramref name="slow"/> is the calm turn of the
    /// status badge while it only checks for changes.
    /// </summary>
    public static void Spin(UIElement e, bool on, bool slow = false)
    {
        // Both turn the same rotation, so the one going off goes first.
        if (!on || slow)
            Loop(e, "spin", false, _ => { }, StopSpin);
        if (!on || !slow)
            Loop(e, "spin-slow", false, _ => { }, StopSpin);
        if (on)
            Loop(e, slow ? "spin-slow" : "spin", true, x => StartSpin(x, slow ? 3.6 : 1.4), StopSpin);
    }

    private static void StartSpin(UIElement e, double seconds) => Transforms(e).Rotate.BeginAnimation(RotateTransform.AngleProperty,
        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });

    private static void StopSpin(UIElement e) => Transforms(e).Rotate.BeginAnimation(RotateTransform.AngleProperty, null);

    /// <summary>Soft breathing (waiting for the other computer).</summary>
    public static void Pulse(UIElement e, bool on) => Loop(e, "pulse", on,
        x =>
        {
            var s = Transforms(x).Scale;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, Wave(1, 1.08, 2));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, Wave(1, 1.08, 2));
        },
        x =>
        {
            var s = Transforms(x).Scale;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        });

    /// <summary>
    /// An arrow that keeps travelling up (uploading) or down (downloading), fading in and out. <paramref name="late"/>
    /// starts half a beat later (the second arrow of the badge while it goes both ways, so the two take turns).
    /// </summary>
    public static void Travel(UIElement e, bool? up, bool late = false, double distance = 5)
    {
        // Off first: both drive the same movement.
        if (up != true)
            Loop(e, "rise", false, _ => { }, StopTravel);
        if (up != false)
            Loop(e, "fall", false, _ => { }, StopTravel);
        if (up is { } rising)
            Loop(e, rising ? "rise" : "fall", true, x => StartTravel(x, rising ? distance : -distance, rising ? -distance : distance, late), StopTravel);
    }

    private static void StartTravel(UIElement e, double from, double to, bool late)
    {
        var duration = TimeSpan.FromSeconds(1.1);
        var begin = late ? TimeSpan.FromSeconds(0.55) : TimeSpan.Zero;
        Transforms(e).Move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, to, duration)
        {
            BeginTime = begin,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        var fade = new DoubleAnimationUsingKeyFrames { Duration = duration, BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.35)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        e.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void StopTravel(UIElement e)
    {
        Transforms(e).Move.BeginAnimation(TranslateTransform.YProperty, null);
        e.BeginAnimation(UIElement.OpacityProperty, null);
    }

    /// <summary>
    /// A ring that keeps growing out of a circle and fading (the other computer's circle while it downloads).
    /// The ring is a separate element behind the circle; it is hidden while off.
    /// </summary>
    public static void Ripple(UIElement ring, bool on) => Loop(ring, "ripple", on,
        x =>
        {
            var duration = TimeSpan.FromSeconds(1.7);
            var grow = new DoubleAnimation(1, 1.42, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var s = Transforms(x).Scale;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            x.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, 0, duration) { RepeatBehavior = RepeatBehavior.Forever });
            x.Visibility = Visibility.Visible;
        },
        x =>
        {
            var s = Transforms(x).Scale;
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            x.BeginAnimation(UIElement.OpacityProperty, null);
            x.Visibility = Visibility.Collapsed;
        });

    /// <summary>One small pop when something changes (status badge, success).</summary>
    public static void Pop(UIElement e)
    {
        if (!Enabled)
            return;
        var s = Transforms(e).Scale;
        var pop = new DoubleAnimation(0.82, 1, TimeSpan.FromSeconds(0.38)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 1 } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>A new row slides in from above.</summary>
    public static void Enter(UIElement e)
    {
        if (!Enabled)
            return;
        var duration = TimeSpan.FromSeconds(0.3);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Transforms(e).Move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, duration) { EasingFunction = ease });
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Moves a progress bar smoothly to <paramref name="value"/> instead of jumping.</summary>
    public static void Glide(ProgressBar bar, double value)
    {
        if (!Enabled || Math.Abs(bar.Value - value) < 0.01)
        {
            bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            bar.Value = value;
            return;
        }
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(value, TimeSpan.FromSeconds(0.35)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Bind a bar's target value here (instead of Value) to make it glide: ui:Motion.SmoothValue="{Binding ...}".</summary>
    public static readonly DependencyProperty SmoothValueProperty = DependencyProperty.RegisterAttached(
        "SmoothValue", typeof(double), typeof(Motion),
        new PropertyMetadata(0.0, (d, e) => { if (d is ProgressBar bar) Glide(bar, (double)e.NewValue); }));

    public static double GetSmoothValue(DependencyObject d) => (double)d.GetValue(SmoothValueProperty);

    public static void SetSmoothValue(DependencyObject d, double value) => d.SetValue(SmoothValueProperty, value);
}
