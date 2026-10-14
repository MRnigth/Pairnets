namespace Pairnets.Core.Client;

/// <summary>The icon a computer is drawn with: what was chosen on the nest, or a guess from its name.</summary>
public enum DeviceIconKind
{
    Desktop,
    Laptop,
    Office,
    Mini,
    Tv,
}

public static class DeviceIcon
{
    /// <summary>Parses the nest's value ("laptop"). Null, empty and unknown values are "not chosen" (false).</summary>
    public static bool TryParse(string? value, out DeviceIconKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case DeviceIcons.Desktop: kind = DeviceIconKind.Desktop; return true;
            case DeviceIcons.Laptop: kind = DeviceIconKind.Laptop; return true;
            case DeviceIcons.Office: kind = DeviceIconKind.Office; return true;
            case DeviceIcons.Mini: kind = DeviceIconKind.Mini; return true;
            case DeviceIcons.Tv: kind = DeviceIconKind.Tv; return true;
            default: kind = DeviceIconKind.Desktop; return false;
        }
    }

    /// <summary>The wire value for a kind ("laptop").</summary>
    public static string ToWire(DeviceIconKind kind) => kind switch
    {
        DeviceIconKind.Laptop => DeviceIcons.Laptop,
        DeviceIconKind.Office => DeviceIcons.Office,
        DeviceIconKind.Mini => DeviceIcons.Mini,
        DeviceIconKind.Tv => DeviceIcons.Tv,
        _ => DeviceIcons.Desktop,
    };

    /// <summary>With nothing chosen: a laptop when the name says LAPTOP or BOOK (MacBook, Chromebook), otherwise a desktop.</summary>
    public static DeviceIconKind Guess(string? name) =>
        name is not null && (name.Contains("LAPTOP", StringComparison.OrdinalIgnoreCase) || name.Contains("BOOK", StringComparison.OrdinalIgnoreCase))
            ? DeviceIconKind.Laptop
            : DeviceIconKind.Desktop;

    /// <summary>The icon to draw for a computer: the chosen one, else the guess.</summary>
    public static DeviceIconKind For(DeviceInfo device) => TryParse(device.Icon, out var kind) ? kind : Guess(device.Name);

    /// <summary>For a nest that may be too old to know icons: always the guess then, so nothing looks chosen that cannot be saved.</summary>
    public static DeviceIconKind For(DeviceInfo device, bool nestSupportsIcons) => nestSupportsIcons ? For(device) : Guess(device.Name);
}
