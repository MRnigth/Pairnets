using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>What a page needs from the main window: the app's actions, going to another page, History on one file.</summary>
public interface IPageHost
{
    IMainActions? Actions { get; }

    void Navigate(MainPage page);

    void ShowVersionsFor(string path);

    /// <summary>Opens "Clean up your nest" on the Files page.</summary>
    void ShowCleanup();

    /// <summary>The "Your nest is full" pop-up.</summary>
    void ShowNestFull();
}

/// <summary>
/// Small builders for the rows the pages draw in code (every colour is a theme key, so a row follows a light or
/// dark switch while it is on screen).
/// </summary>
internal static class Parts
{
    public static TextBlock Text(string text, double size = 14, string ink = "T.Text", FontWeight? weight = null, bool trim = true)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        if (trim)
            block.TextTrimming = TextTrimming.CharacterEllipsis;
        block.SetResourceReference(TextBlock.ForegroundProperty, ink);
        return block;
    }

    public static LineIcon Icon(string geometry, double size, string ink, double thickness = 2)
    {
        var icon = new LineIcon
        {
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            Data = Visuals.Resource<Geometry>(geometry),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(LineIcon.StrokeProperty, ink);
        return icon;
    }

    /// <summary>A rounded square holding an icon (the 34 px chip of a row).</summary>
    public static Border Square(double size, double radius, string fill, UIElement child)
    {
        var square = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = child, VerticalAlignment = VerticalAlignment.Center };
        square.SetResourceReference(Border.BackgroundProperty, fill);
        return square;
    }

    public static Border Chip(string text, string fill = "T.Chip", string ink = "T.Muted", double size = 11)
    {
        var chip = new Border { Height = 20, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(10), Child = Text(text, size, ink, FontWeights.Bold), VerticalAlignment = VerticalAlignment.Center };
        chip.SetResourceReference(Border.BackgroundProperty, fill);
        return chip;
    }

    public static Ellipse Dot(double size, string fill, string? ring = null)
    {
        var dot = new Ellipse { Width = size, Height = size };
        dot.SetResourceReference(Shape.FillProperty, fill);
        if (ring is not null)
        {
            dot.StrokeThickness = 2;
            dot.SetResourceReference(Shape.StrokeProperty, ring);
        }
        return dot;
    }

    /// <summary>A line of icon and text for the inside of a button.</summary>
    public static StackPanel IconText(string geometry, string text, double iconSize = 16, string ink = "T.Text", double thickness = 1.9, double gap = 8)
    {
        var icon = Icon(geometry, iconSize, ink, thickness);
        icon.Margin = new Thickness(0, 0, gap, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
    }

    /// <summary>The icon for a file name: a picture, a video, a sheet, a document or a plain file.</summary>
    public static string FileIcon(string? path) => System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".heic" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".raw" or ".cr2" or ".nef" or ".arw" or ".svg" => "I.Image",
        ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v" => "I.Video",
        ".xlsx" or ".xls" or ".csv" or ".ods" => "I.Sheet",
        ".docx" or ".doc" or ".md" or ".txt" or ".rtf" or ".odt" or ".pdf" => "I.Doc",
        _ => "I.File",
    };

    /// <summary>"10:45", the time of day a row shows.</summary>
    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A boolean to Collapsed when true, Visible when false (the "No limit" note beside a speed-limit switch).</summary>
public sealed class InverseVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
