using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Pairnets.Client.Tests;

/// <summary>What a press does to a control.</summary>
public enum PressKind
{
    /// <summary>A button, switch, check box or radio button, pressed the way screen readers press it.</summary>
    Button,

    /// <summary>An item of a right-click or "…" menu: the menu is opened, the item clicked, the menu closed.</summary>
    MenuItem,

    /// <summary>Something clickable that is not a button (a card that reacts to the left mouse button).</summary>
    MouseUp,

    /// <summary>A list whose selection does something: the next row is selected.</summary>
    Select,

    /// <summary>A search box whose text does something: <see cref="Presser.TypedText"/> is typed.</summary>
    Type,

    /// <summary>A page with its own scrolling: the mouse wheel turns one notch down.</summary>
    Wheel,

    /// <summary>A fold-out section: it opens (or closes).</summary>
    Expand,
}

/// <summary>A control a person can press. <see cref="Handlers"/> are the XAML handlers (Class.OnName) a press runs.</summary>
public sealed record Target(string Label, PressKind Kind, FrameworkElement Element, FrameworkElement? MenuOwner, IReadOnlyList<string> Handlers)
{
    public override string ToString() => $"'{Label}'";
}

/// <summary>
/// Finds every control a person can press in a window (visible and enabled, in screen order) and presses it the
/// way a person would, through the same events. It reads which XAML handler (On…) each control runs off the
/// control itself, so the tests can tell when a handler in the XAML is never pressed.
/// </summary>
public static class Presser
{
    /// <summary>What a press of a <see cref="PressKind.Type"/> target types.</summary>
    public const string TypedText = "report";

    private static MethodInfo? GetHandlers;

    private static readonly PropertyInfo HandlersStore =
        typeof(UIElement).GetProperty("EventHandlersStore", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("WPF changed: UIElement.EventHandlersStore is gone.");

    /// <summary>Every element in the window, depth first in screen order, including those in templates.</summary>
    public static IEnumerable<FrameworkElement> Elements(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is FrameworkElement fe)
                yield return fe;
            if (node is not Visual and not System.Windows.Media.Media3D.Visual3D)
                continue;
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = count - 1; i >= 0; i--)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>Every control a person can press in <paramref name="window"/> right now.</summary>
    public static List<Target> Find(Window window)
    {
        var found = new List<Target>();
        foreach (var e in Elements(window))
        {
            if (!e.IsVisible || IsTemplatePart(e))
                continue;
            switch (e)
            {
                case ButtonBase button when button.IsEnabled:
                    found.Add(new Target(LabelOf(button), PressKind.Button, button, null, HandlersFor(button)));
                    break;
                case Expander expander when expander.IsEnabled:
                    found.Add(new Target(LabelOf(expander), PressKind.Expand, expander, null, []));
                    break;
                case Selector selector when selector.IsEnabled && selector.Items.Count > 0 && Named(selector, Selector.SelectionChangedEvent).Count > 0:
                    found.Add(new Target(LabelOf(selector), PressKind.Select, selector, null, Named(selector, Selector.SelectionChangedEvent)));
                    break;
                case TextBox box when box.IsEnabled && Named(box, TextBoxBase.TextChangedEvent).Count > 0:
                    found.Add(new Target(LabelOf(box), PressKind.Type, box, null, Named(box, TextBoxBase.TextChangedEvent)));
                    break;
                case ScrollViewer scroller when Named(scroller, UIElement.PreviewMouseWheelEvent).Count > 0:
                    found.Add(new Target(LabelOf(scroller), PressKind.Wheel, scroller, null, Named(scroller, UIElement.PreviewMouseWheelEvent)));
                    break;
            }
            if (e is not ButtonBase && Named(e, UIElement.MouseLeftButtonUpEvent) is { Count: > 0 } mouseUp)
                found.Add(new Target(LabelOf(e), PressKind.MouseUp, e, null, mouseUp));
            if (e.ContextMenu is { } menu)
            {
                foreach (var item in MenuItems(menu).Where(i => i.IsEnabled && i.Visibility == Visibility.Visible))
                    found.Add(new Target(LabelOf(item), PressKind.MenuItem, item, e, Named(item, MenuItem.ClickEvent)));
            }
        }
        return found;
    }

    /// <summary>The XAML handlers a button runs: Click, and Checked for radio buttons and switches.</summary>
    private static IReadOnlyList<string> HandlersFor(ButtonBase button) =>
        [.. Named(button, ButtonBase.ClickEvent), .. button is ToggleButton ? Named(button, ToggleButton.CheckedEvent) : []];

    private static IEnumerable<MenuItem> MenuItems(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in MenuItems(item))
                yield return child;
        }
    }

    /// <summary>
    /// A part of a control's own look (a scroll bar's arrows, a fold-out's header), not a control of its own. Rows
    /// drawn from a data template are not: their templated parent is the presenter that shows the row.
    /// </summary>
    private static bool IsTemplatePart(FrameworkElement e) => e.TemplatedParent is Control;

    /// <summary>"Pause", "Show in folder", the tooltip of an icon-only button, or the element's name.</summary>
    public static string LabelOf(FrameworkElement e)
    {
        string? text = e switch
        {
            Selector or TextBox or ScrollViewer or Border => e.Name, // lists, boxes and pages: what they show changes
            MenuItem item => item.Header as string,
            HeaderedContentControl headered => headered.Header as string,
            ContentControl { Content: string s } => s,
            ContentControl { Content: DependencyObject content } => FirstText(content),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text) && e.ToolTip is string tip)
            text = tip;
        if (string.IsNullOrWhiteSpace(text))
            text = e.Name.Length > 0 ? e.Name : e.GetType().Name;
        return text.Trim();
    }

    private static string? FirstText(DependencyObject root) =>
        Elements(root).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
        ?? LogicalTexts(root).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>Texts of content that has not been drawn yet (a menu that never opened).</summary>
    private static IEnumerable<string> LogicalTexts(DependencyObject root)
    {
        if (root is TextBlock t)
            yield return t.Text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var text in LogicalTexts(child))
                yield return text;
        }
    }

    /// <summary>The XAML handlers on <paramref name="element"/> for <paramref name="routedEvent"/>, as "Class.OnName".</summary>
    public static IReadOnlyList<string> Named(UIElement element, RoutedEvent routedEvent)
    {
        if (HandlersStore.GetValue(element) is not { } store)
            return [];
        GetHandlers ??= store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WPF changed: EventHandlersStore.GetRoutedEventHandlers is gone.");
        var handlers = (RoutedEventHandlerInfo[]?)GetHandlers.Invoke(store, [routedEvent]);
        return handlers?
            .Select(h => h.Handler)
            .Where(h => h.Method.Name.StartsWith("On", StringComparison.Ordinal) && h.Target is not null)
            .Select(h => h.Target!.GetType().Name + "." + h.Method.Name)
            .ToList() ?? [];
    }

    /// <summary>Handlers that run by themselves once an element is on screen (row fade-ins), seen in the window.</summary>
    public static IEnumerable<string> LoadedHandlers(Window window) =>
        Elements(window).Where(e => e.IsLoaded).SelectMany(e => Named(e, FrameworkElement.LoadedEvent));

    /// <summary>
    /// Presses <paramref name="target"/> on the WPF thread. A button is pressed through its automation peer (as screen
    /// readers and UI tests do), which queues the click for the dispatcher: let it settle before looking.
    /// Returns the handlers that ran on the way besides the target's own (the menu's Closed handler).
    /// </summary>
    public static IReadOnlyList<string> Press(Target target)
    {
        var e = target.Element;
        switch (target.Kind)
        {
            case PressKind.Button:
                var peer = UIElementAutomationPeer.CreatePeerForElement(e)
                    ?? throw new InvalidOperationException($"{target} has no automation peer.");
                if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                    invoke.Invoke();
                else if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider select)
                    select.Select();
                else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                    toggle.Toggle();
                else
                    throw new InvalidOperationException($"{target} cannot be pressed.");
                return [];
            case PressKind.MenuItem:
                var owner = target.MenuOwner!;
                var menu = owner.ContextMenu!;
                menu.PlacementTarget = owner;
                menu.IsOpen = true;
                e.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, e));
                var closed = Named(menu, ContextMenu.ClosedEvent);
                menu.IsOpen = false;
                return closed;
            case PressKind.MouseUp:
                e.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                    Source = e,
                });
                return [];
            case PressKind.Select:
                var list = (Selector)e;
                list.SelectedIndex = (list.SelectedIndex + 1) % list.Items.Count;
                return [];
            case PressKind.Type:
                ((TextBox)e).Text = TypedText;
                return [];
            case PressKind.Wheel:
                e.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                return [];
            case PressKind.Expand:
                ((Expander)e).IsExpanded = !((Expander)e).IsExpanded;
                return [];
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }

    /// <summary>
    /// Closes the menus a press opened (a "…" button opens its menu), and returns their Closed handlers, which run then.
    /// </summary>
    public static IReadOnlyList<string> CloseMenus(Window window)
    {
        var ran = new List<string>();
        foreach (var menu in Elements(window).Select(e => e.ContextMenu).OfType<ContextMenu>().Where(m => m.IsOpen).ToList())
        {
            ran.AddRange(Named(menu, ContextMenu.ClosedEvent));
            menu.IsOpen = false;
        }
        return ran;
    }
}
