using System.Windows;

namespace Pairnets.Client.Ui;

/// <summary>Attached properties the page styles in Themes/Pages.xaml read (a button's corner radius).</summary>
public static class Pg
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Pg), new FrameworkPropertyMetadata(new CornerRadius(12)));

    public static CornerRadius GetRadius(DependencyObject element) => (CornerRadius)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, CornerRadius value) => element.SetValue(RadiusProperty, value);
}
