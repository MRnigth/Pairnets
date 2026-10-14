using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The drawing for a computer's icon (<see cref="DeviceIconKind"/> to the line-icon geometry in Styles/Icons.axaml).
/// Every place that draws a computer calls this, so a chosen icon shows up the same everywhere.
/// </summary>
public static class DeviceIconArt
{
    public static Geometry? For(DeviceIconKind kind) => Visuals.Resource<Geometry>(DeviceIcon.ResourceKey(kind));

    /// <summary>For a node that may have no icon yet: the desktop.</summary>
    public static Geometry? For(DeviceIconKind? kind) => For(kind ?? DeviceIconKind.Desktop);
}

/// <summary>Binding helper: <see cref="DeviceIconKind"/> to icon geometry (<c>Data="{Binding Icon, Converter=...}"</c>).</summary>
public sealed class DeviceIconConverter : IValueConverter
{
    public static DeviceIconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DeviceIconArt.For(value is DeviceIconKind kind ? kind : DeviceIconKind.Desktop);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
