using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Tether.Desktop.Views;

/// <summary>
/// Plays the one-time animations in Styles/Controls.axaml (pop, ripple, wiggle, turn, arrive,
/// burst), same as the Windows app's Motion. Each is a class that animates when it is added.
/// </summary>
public static class Motion
{
    private static readonly ConditionalWeakTable<Control, Dictionary<string, int>> Played = new();

    /// <summary>Plays <paramref name="name"/> again: the class comes off, and goes back on once that was seen.</summary>
    public static void Once(Control control, string name)
    {
        var counts = Played.GetOrCreateValue(control);
        counts[name] = counts.GetValueOrDefault(name) + 1;
        control.Classes.Remove(name);
        Dispatcher.UIThread.Post(() => control.Classes.Add(name), DispatcherPriority.Background);
    }

    /// <summary>How many times <paramref name="name"/> was played on <paramref name="control"/> (the headless UI test).</summary>
    internal static int Plays(Control control, string name) =>
        Played.TryGetValue(control, out var counts) ? counts.GetValueOrDefault(name) : 0;
}
