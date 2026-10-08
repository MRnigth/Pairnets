using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Desktop.Views;

/// <summary>Colours and icons for statuses and activity kinds (keys in Styles/Palette.axaml and Icons.axaml).</summary>
public static class Visuals
{
    public static (string Brush, string Icon) ForStatus(RunnerStatus status) => status switch
    {
        RunnerStatus.Idle => ("S.Green", "I.Check"),
        RunnerStatus.Syncing => ("S.Blue", "I.Sync"),
        RunnerStatus.Offline => ("S.Grey", "I.CloudOff"),
        RunnerStatus.Paused => ("S.Grey", "I.Pause"),
        RunnerStatus.Blocked => ("S.Orange", "I.Bang"),
        _ => ("S.Red", "I.X"),
    };

    public static (string Brush, string Icon) ForActivity(ActivityKind kind) => kind switch
    {
        ActivityKind.Uploaded => ("S.Green", "I.Up"),
        ActivityKind.Downloaded => ("S.Blue", "I.Down"),
        ActivityKind.DeletedHere or ActivityKind.DeletedOnServer => ("S.Red", "I.X"),
        ActivityKind.Conflict => ("S.Orange", "I.Warning"),
        ActivityKind.Warning or ActivityKind.Error => ("S.Red", "I.Bang"),
        ActivityKind.Blocked => ("S.Orange", "I.Pause"),
        ActivityKind.Offline => ("S.Grey", "I.CloudOff"),
        _ => ("S.Grey", "I.Info"),
    };

    public static T? Resource<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as T : null;

    /// <summary>
    /// Points a property at a theme resource, so it follows a switch between light and dark (unlike
    /// <see cref="Resource{T}"/>, which reads the colour once).
    /// </summary>
    public static void Bind(StyledElement element, AvaloniaProperty property, string key) =>
        element.Bind(property, element.GetResourceObservable(key));
}

/// <summary>ActivityKind → status brush.</summary>
public sealed class ActivityBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ActivityKind k ? Visuals.Resource<IBrush>(Visuals.ForActivity(k).Brush) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>ActivityKind → icon geometry.</summary>
public sealed class ActivityIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ActivityKind k ? Visuals.Resource<Geometry>(Visuals.ForActivity(k).Icon) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
