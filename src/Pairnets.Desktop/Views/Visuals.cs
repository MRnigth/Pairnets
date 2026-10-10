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
        RunnerStatus.Paused => ("S.Yellow", "I.Pause"),
        RunnerStatus.Blocked => ("S.Orange", "I.Bang"),
        _ => ("S.Red", "I.X"),
    };

    /// <summary>
    /// The status badge's colour and icon: while syncing, what this computer really does (green arrow up, blue arrow
    /// down, orange when stalled); otherwise the status.
    /// </summary>
    public static (string Brush, string Icon) ForBadge(StatusSnapshot s) => s.IsWaiting ? ("S.Grey", "I.Wait") : s.Motion switch
    {
        SyncMotion.Up => ("S.Green", "I.Up"),
        SyncMotion.Down => ("S.Blue", "I.Down"),
        SyncMotion.Both => ("S.Blue", "I.Up"),
        SyncMotion.Stalled => ("S.Orange", s.GoingUp ? "I.Up" : "I.Down"),
        _ => ForStatus(s.Status),
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
