using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Pairnets.Desktop.Platform;

namespace Pairnets.Desktop.Views;

/// <summary>
/// Small, friendly animations (same as the Windows app's Ui\Motion.cs): bob, spin, pulse, ripple, pop, travelling
/// arrows and rows sliding in are style classes in Styles/Controls.axaml; the dots on the lines and the progress bars
/// move from code. Everything stays still when the system asks for less motion (macOS "Reduce motion", GNOME
/// animations off; Windows "Show animations" off when this app runs there for tests).
/// </summary>
public static class Motion
{
    private static bool? _enabled;
    private static readonly List<Window> Tracked = [];
    private static DispatcherTimer? _graceTimer;

    /// <summary>How long motion goes on after the last window stopped being the one in use (see <see cref="MotionGate"/>).</summary>
    public static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(6);

    /// <summary>Whether a window is on screen and in use (or was a moment ago). Tests replace it with a gate of their own clock.</summary>
    internal static MotionGate Gate { get; set; } = new(TimeProvider.System, IdleGrace);

    /// <summary>
    /// True while motion is worth drawing. Nothing is tracked until an animated control is on a window, so before that, and
    /// in a test that has no real window, it is true.
    /// </summary>
    public static bool Live => Tracked.Count == 0 || Gate.Live;

    /// <summary>Raised when <see cref="Live"/> changes: the loops that draw frames start or stop.</summary>
    public static event Action? LiveChanged;

    /// <summary>
    /// Watches a window: the infinite animations of the app's styles run only under a window with the class "live", and the
    /// frame loops (<see cref="FlowLine"/>, <see cref="HubView"/>) only while <see cref="Live"/>.
    /// </summary>
    public static void Track(Window window)
    {
        if (Tracked.Contains(window))
            return;
        if (Tracked.Count == 0)
            Gate.Changed += OnGateChanged;
        Tracked.Add(window);
        window.Activated += OnWindowChanged;
        window.Deactivated += OnWindowChanged;
        window.Closed += OnWindowClosed;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
                OnWindowChanged(null, EventArgs.Empty);
        };
        OnWindowChanged(null, EventArgs.Empty);
        window.Classes.Set("live", Gate.Live);
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
            Tracked.Remove(window);
        OnWindowChanged(null, EventArgs.Empty);
    }

    private static void OnWindowChanged(object? sender, EventArgs e)
    {
        Gate.Report(Tracked.Any(w => w.IsVisible && w.WindowState != WindowState.Minimized), Tracked.Any(w => w.IsActive));
        ScheduleEnd();
    }

    private static void OnGateChanged()
    {
        foreach (var window in Tracked)
            window.Classes.Set("live", Gate.Live);
        LiveChanged?.Invoke();
    }

    /// <summary>One timer for the end of the grace: when no window is in use, motion stops a few seconds later.</summary>
    private static void ScheduleEnd()
    {
        _graceTimer?.Stop();
        if (Gate.EndsAt is not { } end)
            return;
        var wait = end - TimeProvider.System.GetUtcNow();
        _graceTimer = new DispatcherTimer(wait > TimeSpan.Zero ? wait + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            _graceTimer?.Stop();
            Gate.Evaluate();
        });
        _graceTimer.Start();
    }

    /// <summary>False when the system asks for less motion (read once, the first time it is needed). Tests set it.</summary>
    public static bool Enabled
    {
        get => _enabled ??= !ReduceMotion();
        internal set => _enabled = value;
    }

    private static bool ReduceMotion()
    {
        try
        {
            return IPlatformServices.Current.ReduceMotion;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Switches an animation class (bob, spin, pulse, ...) on or off; it stays off while motion is reduced.</summary>
    public static void Set(StyledElement e, string name, bool on)
    {
        e.Classes.Set(name, on && Enabled);
        if (!on || e is not Visual visual)
            return;
        if (TopLevel.GetTopLevel(visual) is Window window)
            Track(window);
        else if (visual is Control control)
            control.AttachedToVisualTree += TrackWhenAttached; // not on a window yet: its animations start once it is
    }

    private static void TrackWhenAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
        {
            control.AttachedToVisualTree -= TrackWhenAttached;
            if (TopLevel.GetTopLevel(control) is Window window)
                Track(window);
        }
    }

    /// <summary>Plays a one-off animation class (pop) again: off now, back on after the next layout.</summary>
    public static void Replay(StyledElement e, string name)
    {
        e.Classes.Remove(name);
        if (Enabled)
            Dispatcher.UIThread.Post(() => e.Classes.Add(name), DispatcherPriority.Background);
    }

    /// <summary>
    /// Called once at start. With less motion, the animations of the app's styles are taken out (so the classes
    /// written in XAML, like a row's "enter", do nothing) and progress bars jump to their value instead of gliding.
    /// </summary>
    public static void Apply(Application app)
    {
        if (Enabled)
            return;
        foreach (var style in app.Styles)
            Still(style);
        app.Styles.Add(new Style(x => x.OfType<ProgressBar>()) { Setters = { new Setter(Animatable.TransitionsProperty, new Transitions()) } });
    }

    private static void Still(IStyle style)
    {
        if (style is Style s)
            s.Animations.Clear();
        if (style is Avalonia.Markup.Xaml.Styling.StyleInclude { Loaded: { } loaded })
            Still(loaded);
        foreach (var child in style.Children)
            Still(child);
    }
}
