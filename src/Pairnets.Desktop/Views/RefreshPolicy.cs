namespace Pairnets.Desktop.Views;

/// <summary>
/// When the controller redraws the menu, the window and the panel from the status. While a window is in use that is
/// every 250 ms a change is waiting (progress moves quickly during a sync); while no window is on screen or in use,
/// every two seconds at most: the tray tooltip and menu need no more, and a sync of thousands of files reports changes
/// all the time.
/// </summary>
public static class RefreshPolicy
{
    /// <summary>Timer ticks (250 ms) between redraws when nobody looks.</summary>
    public const int IdleEvery = 8;

    public static bool Due(int tick, bool inUse) => inUse || tick % IdleEvery == 0;
}
