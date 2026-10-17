using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Pairnets.Client.Ui;

/// <summary>
/// Gives every button a name screen readers read out. WPF names a button only when its content is plain text; most
/// of ours show an icon and a text (Pause, Sync now, the page tiles), so they had none. Each button without a name of
/// its own gets its first text, kept in step when that text changes (Pause → Resume), or else its tooltip.
/// </summary>
public static class ScreenReaderNames
{
    private static bool _registered;

    /// <summary>Once per process: every button that is laid out from then on is named.</summary>
    /// <remarks>
    /// On its first layout, when its look and content are in place. (WPF sends Loaded only to elements with a Loaded
    /// handler of their own, so a class handler for Loaded never runs; SizeChanged reaches every element.)
    /// </remarks>
    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnLaidOut));
    }

    private static void OnLaidOut(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ButtonBase button || button.Content is string
            || !string.IsNullOrEmpty(AutomationProperties.GetName(button))
            || BindingOperations.IsDataBound(button, AutomationProperties.NameProperty))
            return;
        if (FirstText(button) is { } text)
            button.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(TextBlock.Text)) { Source = text });
        else if (button.ToolTip is string)
            button.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(ButtonBase.ToolTip)) { Source = button });
    }

    /// <summary>The first text block with words in it, in the order a person reads them.</summary>
    private static TextBlock? FirstText(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Visibility: Visibility.Visible } text && !string.IsNullOrWhiteSpace(text.Text))
                return text;
            if (FirstText(child) is { } found)
                return found;
        }
        return null;
    }
}
