using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Ui;

/// <summary>Colours and icons for statuses and activity kinds (keys in Themes/Light.xaml, Dark.xaml and Icons.xaml).</summary>
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

    public static T? Resource<T>(string key) where T : class => Application.Current?.TryFindResource(key) as T;
}

/// <summary>
/// The app's own icon, read from pairnets.ico built into the app (not from Pairnets.exe, whose icon
/// Windows caches per path and keeps showing the old one after an update).
/// </summary>
public static class AppIcons
{
    private static readonly Lazy<System.Windows.Media.Imaging.BitmapFrame?[]> Frames = new(Load);

    /// <summary>For Window.Icon: the whole icon, so the taskbar and title bar pick their own size.</summary>
    public static ImageSource? WindowIcon => Frames.Value[0];

    /// <summary>The largest size, for the rail and the sign-in screen.</summary>
    public static ImageSource? Large => Frames.Value[1];

    private static System.Windows.Media.Imaging.BitmapFrame?[] Load()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Pairnets;component/pairnets.ico"));
            if (info is null)
                return [null, null];
            using var stream = info.Stream;
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var whole = decoder.Frames[0];
            var largest = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            whole.Freeze();
            largest.Freeze();
            return [whole, largest];
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            return [null, null];
        }
    }
}

/// <summary>ActivityKind → icon geometry.</summary>
public sealed class ActivityIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ActivityKind k ? Visuals.Resource<Geometry>(Visuals.ForActivity(k).Icon) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → Visible, false → Collapsed (or the other way round with <see cref="Invert"/>).</summary>
public sealed class VisibleConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
