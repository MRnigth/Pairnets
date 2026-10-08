using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pairnets.Client.Ui;

/// <summary>
/// Attached properties the styles in Themes/Controls.xaml read: rounded corners and hover colours for the
/// one button template, the "open" state of a rail button, and the grey hint text in an empty text box.
/// </summary>
public static class Look
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Look), new FrameworkPropertyMetadata(new CornerRadius(10)));

    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Look), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty HoverBorderBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBorderBrush", typeof(Brush), typeof(Look), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(Look), new FrameworkPropertyMetadata(null));

    /// <summary>A rail button whose menu is open (drawn as chosen).</summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(Look), new FrameworkPropertyMetadata(false));

    /// <summary>Grey hint text shown while a text box (or password box) is empty.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Look), new FrameworkPropertyMetadata(null));

    /// <summary>True while a password box is empty (kept up to date for every password box).</summary>
    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.RegisterAttached(
        "IsEmpty", typeof(bool), typeof(Look), new FrameworkPropertyMetadata(true));

    // Runs as soon as the styles read one of the properties above, before any password box is shown.
    static Look()
    {
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler((sender, _) => SetIsEmpty((PasswordBox)sender, ((PasswordBox)sender).Password.Length == 0)));
    }

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    public static Brush? GetHoverBackground(DependencyObject d) => (Brush?)d.GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject d, Brush? value) => d.SetValue(HoverBackgroundProperty, value);

    public static Brush? GetHoverBorderBrush(DependencyObject d) => (Brush?)d.GetValue(HoverBorderBrushProperty);

    public static void SetHoverBorderBrush(DependencyObject d, Brush? value) => d.SetValue(HoverBorderBrushProperty, value);

    public static Brush? GetPressedBackground(DependencyObject d) => (Brush?)d.GetValue(PressedBackgroundProperty);

    public static void SetPressedBackground(DependencyObject d, Brush? value) => d.SetValue(PressedBackgroundProperty, value);

    public static bool GetIsOpen(DependencyObject d) => (bool)d.GetValue(IsOpenProperty);

    public static void SetIsOpen(DependencyObject d, bool value) => d.SetValue(IsOpenProperty, value);

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

    public static bool GetIsEmpty(DependencyObject d) => (bool)d.GetValue(IsEmptyProperty);

    public static void SetIsEmpty(DependencyObject d, bool value) => d.SetValue(IsEmptyProperty, value);
}
