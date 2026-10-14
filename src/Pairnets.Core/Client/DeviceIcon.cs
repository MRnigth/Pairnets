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

    /// <summary>The icon for a computer known only by name (this computer in the left side): the one the nest holds, else the guess.</summary>
    public static DeviceIconKind ForName(StatusSnapshot? s, string name)
    {
        var device = s?.Devices?.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        return device is null ? Guess(name) : For(device, s!.DeviceIconsSupported);
    }

    /// <summary>The name of the line-icon drawing in each app's icon dictionary ("I.Laptop"), the same in both apps.</summary>
    public static string ResourceKey(DeviceIconKind kind) => kind switch
    {
        DeviceIconKind.Laptop => "I.Laptop",
        DeviceIconKind.Office => "I.Office",
        DeviceIconKind.Mini => "I.MiniPc",
        DeviceIconKind.Tv => "I.Tv",
        _ => "I.Desktop",
    };

    /// <summary>What the chooser calls each icon.</summary>
    public static string Label(DeviceIconKind kind) => kind switch
    {
        DeviceIconKind.Laptop => "Laptop",
        DeviceIconKind.Office => "Office",
        DeviceIconKind.Mini => "Mini PC",
        DeviceIconKind.Tv => "TV / media",
        _ => "Desktop",
    };

    /// <summary>The five choices, in the order the chooser shows them.</summary>
    public static IReadOnlyList<DeviceIconKind> Choices { get; } =
        [DeviceIconKind.Desktop, DeviceIconKind.Laptop, DeviceIconKind.Office, DeviceIconKind.Mini, DeviceIconKind.Tv];
}
