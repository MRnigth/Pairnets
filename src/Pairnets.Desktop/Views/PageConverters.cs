using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>"Conflict,Blocked" as the parameter: true when the bound enum value is one of those names.</summary>
public sealed class KindIs : IValueConverter
{
    public static readonly KindIs Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string names && names.Split(',').Contains(value.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The time of day ("10:45") of a moment, in the local time zone.</summary>
public sealed class ClockText : IValueConverter
{
    public static readonly ClockText Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset at ? at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Capitals for the small headings ("TODAY"): neither app can uppercase text in its styles.</summary>
public sealed class UpperText : IValueConverter
{
    public static readonly UpperText Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(CultureInfo.CurrentCulture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The first N items of a list (N is the parameter).</summary>
public sealed class FirstItems : IValueConverter
{
    public static readonly FirstItems Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = parameter is string s && int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : 2;
        return value is IEnumerable items ? items.Cast<object>().Take(count).ToList() : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"Show all 37 photos" from the folder row's count text, with the preview already shown.</summary>
public sealed class ShowAllText : IValueConverter
{
    public static readonly ShowAllText Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string count ? "Show all " + count : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when a text is not empty.</summary>
public sealed class HasText : IValueConverter
{
    public static readonly HasText Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"today 10:41" for a moment (empty when it is not known).</summary>
public sealed class MomentText : IValueConverter
{
    public static readonly MomentText Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset at ? Format.Moment(at, DateTimeOffset.Now) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"Code on PC-1" for a join card whose title is "PC-1 (Windows) wants to join".</summary>
public sealed class JoinComputer : IValueConverter
{
    public static readonly JoinComputer Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string title)
            return null;
        var end = title.IndexOf(" (", StringComparison.Ordinal);
        if (end < 0)
            end = title.IndexOf(" wants", StringComparison.Ordinal);
        return "Code on " + (end > 0 ? title[..end] : "that computer");
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
