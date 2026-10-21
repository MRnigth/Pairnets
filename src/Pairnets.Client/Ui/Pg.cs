using System.Windows;

namespace Pairnets.Client.Ui;

/// <summary>
/// Attached properties the button styles in Themes/Pages.xaml and Controls.xaml read: a button's corner radius, and
/// the colour it takes while pressed (the same colours as the Mac/Linux app).
/// </summary>
public static class Pg
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Pg), new FrameworkPropertyMetadata(new CornerRadius(12)));

    public static CornerRadius GetRadius(DependencyObject element) => (CornerRadius)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, CornerRadius value) => element.SetValue(RadiusProperty, value);

    public static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(System.Windows.Media.Brush), typeof(Pg), new FrameworkPropertyMetadata(null));

    public static System.Windows.Media.Brush? GetPressed(DependencyObject element) => (System.Windows.Media.Brush?)element.GetValue(PressedProperty);

    public static void SetPressed(DependencyObject element, System.Windows.Media.Brush? value) => element.SetValue(PressedProperty, value);
}
