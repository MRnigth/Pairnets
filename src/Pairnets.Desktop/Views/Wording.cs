using Avalonia.Data.Converters;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The shared texts say "this PC", the Windows app's word. This app runs on Macs and Linux, so it says "this computer"
/// (the Mac test, 2026-10-24, found "On this PC" on the Files page).
/// </summary>
public static class Wording
{
    public static string Computer(string text) =>
        text.Replace("this PC", "this computer", StringComparison.Ordinal).Replace("This PC", "This computer", StringComparison.Ordinal);

    /// <summary>For a binding: "On this PC" → "On this computer".</summary>
    public static IValueConverter Converter { get; } = new FuncValueConverter<string?, string>(t => Computer(t ?? string.Empty));
}
