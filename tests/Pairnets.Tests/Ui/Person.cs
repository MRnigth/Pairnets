using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Pairnets.Tests.Ui;

/// <summary>A control a person cannot press: hidden, greyed out, covered, or outside its window.</summary>
public sealed class CannotPressException(string message) : Exception(message);

/// <summary>
/// A person at the (headless) screen. Clicks go in as real mouse input at the middle of the control, so a click
/// only lands when the control is shown, enabled and not covered by something else; typing and scrolling go in
/// as real input too. Every press runs the way the app runs it, with Avalonia's own synchronization context, so a
/// crash in an async click handler reaches <see cref="Dispatcher.UnhandledException"/> as it would in the app.
/// </summary>
public static class Person
{
    public static void Click(Control control) => Press(control, MouseButton.Left);

    /// <summary>A right click (opens a context menu).</summary>
    public static void RightClick(Control control) => Press(control, MouseButton.Right);

    private static void Press(Control control, MouseButton button)
    {
        var (top, at) = Reach(control);
        Input(() =>
        {
            top.MouseMove(at);
            top.MouseDown(at, button);
            top.MouseUp(at, button);
        });
    }

    /// <summary>Clicks into a text field, replaces what is in it and types <paramref name="text"/>.</summary>
    public static void Type(TextBox box, string text)
    {
        Click(box);
        var top = TopLevel.GetTopLevel(box)!;
        Input(() =>
        {
            box.SelectAll();
            if (text.Length == 0)
            {
                top.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                top.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            }
            else
                top.KeyTextInput(text);
        });
    }

    /// <summary>Opens or closes an expander by clicking its header.</summary>
    public static void Expand(Expander expander) =>
        Click(expander.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault()
            ?? throw new CannotPressException($"'{Screen.Describe(expander)}' has no header to click."));

    /// <summary>Turns the mouse wheel over a control (negative notches scroll down).</summary>
    public static void Scroll(Control over, double notches = -3)
    {
        var (top, at) = Reach(over);
        Input(() => top.MouseWheel(at, new Vector(0, notches)));
    }

    /// <summary>A click on an item of the tray / menu-bar menu, through the same call the operating system's menu makes.</summary>
    public static void Click(NativeMenuItem item)
    {
        if (!item.IsEnabled)
            throw new CannotPressException($"The menu item '{item.Header}' is greyed out.");
        if (!item.IsVisible)
            throw new CannotPressException($"The menu item '{item.Header}' is hidden.");
        Input(((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked);
    }

    /// <summary>Runs queued work, lays the windows out and lets the clock-driven animations finish.</summary>
    public static void WaitForAnimations()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(450); // rows fade and slide in on the real clock, even headless
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Input(Action action)
    {
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
        try
        {
            action();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }

    /// <summary>Where a click on <paramref name="control"/> goes in, or why it cannot.</summary>
    private static (TopLevel Top, Point At) Reach(Control control)
    {
        var name = Screen.Describe(control);
        if (TopLevel.GetTopLevel(control) is not { } top)
            throw new CannotPressException($"'{name}' is not in a window.");
        if (!control.IsEffectivelyVisible)
            throw new CannotPressException($"'{name}' is hidden.");
        if (!control.IsEffectivelyEnabled)
            throw new CannotPressException($"'{name}' is greyed out.");
        string why = "it was never laid out";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                WaitForAnimations();
            control.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            top.UpdateLayout();
            if (control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top) is not { } at)
                continue;
            if (at.X < 0 || at.Y < 0 || at.X >= top.Bounds.Width || at.Y >= top.Bounds.Height)
            {
                why = "it is outside its window";
                continue;
            }
            var hit = top.InputHitTest(at) as Visual;
            if (hit is not null && (hit == control || control.IsVisualAncestorOf(hit)))
                return (top, at);
            why = hit is null ? "nothing answers a click there" : $"'{Screen.Describe(hit)}' covers it";
        }
        throw new CannotPressException($"'{name}' cannot be clicked: {why}.");
    }
}

/// <summary>Reads the screen: what a control says, the controls a person could press, and the handlers behind them.</summary>
public static class Screen
{
    /// <summary>What a person reads on a control: its text, else its tooltip, else its name.</summary>
    public static string Describe(Visual visual)
    {
        // Only what a person reads on something they press; a page or a window is named, not read out.
        var text = visual switch
        {
            Window w => w.Title,
            ToggleSwitch s => TextOf(s.OnContent ?? s.Content),
            MenuItem m => TextOf(m.Header),
            HeaderedContentControl h => TextOf(h.Header),
            Button or ListBoxItem => TextOf(((ContentControl)visual).Content) ?? TextOf(visual), // a data row reads as its texts
            TextBox t => t.Watermark ?? t.Name,
            TextBlock t => t.Text,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text) && visual is Control control && ToolTip.GetTip(control) is { } tip)
            text = TextOf(tip);
        if (string.IsNullOrWhiteSpace(text))
            text = (visual as StyledElement)?.Name;
        return string.IsNullOrWhiteSpace(text) ? visual.GetType().Name : text.Trim();
    }

    private static string? TextOf(object? content) => content switch
    {
        null => null,
        string s => s,
        TextBlock t => t.Text,
        Visual v => string.Join(" ", v.GetVisualDescendants().Concat(v.GetLogicalDescendants().OfType<Visual>()).OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct()) is { Length: > 0 } text ? text : null,
        _ => null,
    };

    /// <summary>
    /// Every control of type <typeparamref name="T"/> in <paramref name="root"/>: its visual tree (open menus included)
    /// and its logical tree (content that is not shown yet, such as a closed expander's).
    /// </summary>
    public static IEnumerable<T> All<T>(Visual root) where T : Visual =>
        root.GetSelfAndVisualDescendants().Concat(root.GetSelfAndLogicalDescendants().OfType<Visual>()).Distinct().OfType<T>();

    /// <summary>The one shown control of type <typeparamref name="T"/> that says <paramref name="label"/>.</summary>
    public static T Find<T>(Visual root, string label) where T : Control
    {
        var found = All<T>(root).Where(c => c.IsAttachedToVisualTree() && c.IsEffectivelyVisible && Describe(c) == label).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new CannotPressException($"There is no '{label}' ({typeof(T).Name}) on {Describe(root)}. Shown: "
                + string.Join(", ", All<T>(root).Where(c => c.IsEffectivelyVisible).Select(c => "'" + Describe(c) + "'").Distinct())),
            _ => found[0], // the same button in several rows: any of them
        };
    }

    /// <summary>A named control of a window (x:Name).</summary>
    public static T Named<T>(Visual root, string name) where T : Control =>
        All<T>(root).FirstOrDefault(c => c.Name == name) ?? throw new CannotPressException($"{Describe(root)} has no control named {name}.");

    private static readonly FieldInfo? HandlersField = typeof(Interactive).GetField("_eventHandlers", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// The app's own handlers for <paramref name="routedEvent"/> on a control, as "View.Method" (for example
    /// "MainWindow.OnSyncNow"), read from the subscriptions Avalonia keeps. Framework handlers are left out.
    /// </summary>
    public static IReadOnlyList<string> Handlers(Interactive target, RoutedEvent routedEvent)
    {
        if (HandlersField is null)
            throw new InvalidOperationException("Avalonia no longer keeps event handlers in Interactive._eventHandlers; update Screen.Handlers.");
        if (HandlersField.GetValue(target) is not IDictionary all || !all.Contains(routedEvent))
            return [];
        var names = new List<string>();
        foreach (var subscription in (IEnumerable)all[routedEvent]!)
        {
            if (subscription.GetType().GetProperty("Handler")?.GetValue(subscription) is Delegate handler && AppMethod(handler.Method) is { } name)
                names.Add(name);
        }
        return names;
    }

    /// <summary>The app's handlers of a plain .NET event (for example a flyout's Opened), as "View.Method".</summary>
    public static IReadOnlyList<string> EventHandlers(object target, string eventName)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance) is { } field && typeof(Delegate).IsAssignableFrom(field.FieldType))
                return (field.GetValue(target) as Delegate)?.GetInvocationList().Select(d => AppMethod(d.Method)).OfType<string>().ToList() ?? [];
        }
        return [];
    }

    /// <summary>"MainWindow.OnSyncNow" for a handler the app declares; null for framework code.</summary>
    private static string? AppMethod(MethodInfo method)
    {
        var type = method.DeclaringType;
        while (type?.DeclaringType is not null && type.Name.StartsWith('<'))
            type = type.DeclaringType; // a lambda: name the class it is written in
        return type?.Namespace?.StartsWith("Pairnets.Desktop", StringComparison.Ordinal) == true ? $"{type.Name}.{method.Name}" : null;
    }

    /// <summary>True when something happens on a click: an app handler, a command, or a menu that opens.</summary>
    public static bool DoesSomething(Control control) => control switch
    {
        MenuItem item => Handlers(item, MenuItem.ClickEvent).Count > 0 || item.Command is not null || item.ItemCount > 0,
        Button button => Handlers(button, Button.ClickEvent).Count > 0 || button.Command is not null || button.Flyout is not null,
        _ => true,
    };

    /// <summary>The window a control belongs to (open menus belong to their window).</summary>
    public static Window? WindowOf(Visual visual) => TopLevel.GetTopLevel(visual) as Window;
}

/// <summary>Waiting for the app, with a plain-English message when it does not happen.</summary>
public static class Wait
{
    public static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Waited {seconds} seconds, but {what} did not happen.");
            await Task.Delay(50);
        }
    }

    /// <summary>Waits for a value to appear.</summary>
    public static async Task<T> For<T>(Func<T?> value, string what, int seconds = 20) where T : class
    {
        T? found = null;
        await Until(() => (found = value()) is not null, what, seconds);
        return found!;
    }
}
