namespace Pairnets.Desktop.Views;

/// <summary>
/// Whether motion is worth drawing right now: a window of Pairnets is on screen (shown, not minimized) and either the
/// person is working in it or did a moment ago (<see cref="Grace"/>). A menu-bar app that only syncs must not keep
/// redrawing dots and spinners into a window nobody looks at: on a Mac every frame is a trip through the graphics
/// driver, and Pairnets 1.0.59 used half a processor for minutes behind other windows (macOS "CPU resource" report).
/// </summary>
public sealed class MotionGate(TimeProvider clock, TimeSpan grace)
{
    private bool _visible;
    private bool _active;
    private DateTimeOffset _lastActive;
    private bool _live;

    /// <summary>How long motion goes on after the last window stopped being the one in use.</summary>
    public TimeSpan Grace { get; } = grace;

    /// <summary>True while motion should be drawn.</summary>
    public bool Live => _live;

    /// <summary>When <see cref="Live"/> will end by itself if nothing changes (the window is visible but not in use); null otherwise.</summary>
    public DateTimeOffset? EndsAt => _live && !_active ? _lastActive + Grace : null;

    /// <summary>Raised when <see cref="Live"/> changes.</summary>
    public event Action? Changed;

    /// <param name="visible">Some window is shown and not minimized.</param>
    /// <param name="active">Some window of the app is the active one.</param>
    public void Report(bool visible, bool active)
    {
        _visible = visible;
        _active = active;
        if (active)
            _lastActive = clock.GetUtcNow();
        else if (visible && !_live && _lastActive == default)
            _lastActive = clock.GetUtcNow(); // shown without being activated: a moment of motion all the same
        Evaluate();
    }

    /// <summary>Looks again at the clock (a timer calls this when <see cref="EndsAt"/> has passed).</summary>
    public void Evaluate()
    {
        var live = _visible && (_active || clock.GetUtcNow() - _lastActive < Grace);
        if (live == _live)
            return;
        _live = live;
        Changed?.Invoke();
    }
}
