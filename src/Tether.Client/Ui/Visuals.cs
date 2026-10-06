using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Tether.Core.Client;
using Tether.Core.Sync;

namespace Tether.Client.Ui;

/// <summary>Colours and icons for statuses and activity kinds (keys in Themes/Icons.xaml).</summary>
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

    /// <summary>A soft wash of a status colour from the top, fading out by <paramref name="end"/> (0–1).</summary>
    public static Brush Wash(string brushKey, byte alpha, double end)
    {
        var color = (Resource<SolidColorBrush>(brushKey))?.Color ?? Colors.Gray;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), end));
        brush.Freeze();
        return brush;
    }
}

/// <summary>The app's own icon as an image (taken from Tether.exe), for the windows' headers.</summary>
public static class AppIcons
{
    private static ImageSource? _large;
    private static bool _loaded;

    public static ImageSource? Large
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                _large = Load();
            }
            return _large;
        }
    }

    private static ImageSource? Load()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is null)
                return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;
            using var bitmap = icon.ToBitmap();
            var hbitmap = bitmap.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

/// <summary>ActivityKind → status brush.</summary>
public sealed class ActivityBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ActivityKind k ? Visuals.Resource<Brush>(Visuals.ForActivity(k).Brush) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>ActivityKind → icon geometry.</summary>
public sealed class ActivityIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ActivityKind k ? Visuals.Resource<Geometry>(Visuals.ForActivity(k).Icon) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Upload (true) → green, download → blue: the colour of a file's progress bar.</summary>
public sealed class DirectionBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Visuals.Resource<Brush>(value is true ? "S.Green" : "S.Blue");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
