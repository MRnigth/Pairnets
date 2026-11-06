using System.Windows;

namespace Pairnets.Client.Ui;

/// <summary>
/// Whether the app may animate: Windows' "Show animations in Windows" switch (<see cref="SystemParameters.ClientAreaAnimation"/>).
/// The tests set <see cref="ForTests"/> to run the windows once with animations on and once off, whatever this computer's
/// setting is, without touching the setting itself.
/// </summary>
internal static class AnimationSetting
{
    /// <summary>Null: follow Windows. Only the tests set it (for the whole run, in their own process).</summary>
    internal static bool? ForTests { get; set; }

    internal static bool Enabled => ForTests ?? SystemParameters.ClientAreaAnimation;
}
