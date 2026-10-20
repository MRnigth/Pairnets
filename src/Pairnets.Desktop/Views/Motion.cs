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
    public static void Set(StyledElement e, string name, bool on) => e.Classes.Set(name, on && Enabled);

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
