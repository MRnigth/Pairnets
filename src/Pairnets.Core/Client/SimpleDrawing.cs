using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>
/// "Simple window drawing": Pairnets' windows drawn without the graphics card. Off unless asked for, because on some PCs
/// (a graphics overlay or driver that breaks how a window reaches the screen) a window otherwise opens blank. Asked for by
/// the setting (the tray menu), the <see cref="Argument"/> start-up argument or the <see cref="EnvironmentVariable"/>.
/// </summary>
public static class SimpleDrawing
{
    /// <summary>Start-up argument for support: draw the windows simply this time, without changing the setting.</summary>
    public const string Argument = "--software-drawing";

    /// <summary>Environment variable for support: "1" (or "true") draws the windows simply.</summary>
    public const string EnvironmentVariable = "PAIRNETS_SOFTWARE_DRAWING";

    /// <summary>True when the windows should be drawn simply: the setting, the start-up argument or the environment variable.</summary>
    public static bool IsWanted(ClientSettings settings, IEnumerable<string> args, string? environmentValue) =>
        settings.SoftwareDrawing
        || args.Contains(Argument, StringComparer.OrdinalIgnoreCase)
        || environmentValue?.Trim() is "1" || string.Equals(environmentValue?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
