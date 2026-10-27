namespace Pairnets.Core.Sync;

/// <summary>Waits that grow while something keeps failing, so a stuck retry never becomes a tight loop.</summary>
public static class Backoff
{
    /// <summary><paramref name="baseDelay"/> doubled <paramref name="attempt"/> times (0 = the first retry), never above <paramref name="cap"/>.</summary>
    public static TimeSpan Doubling(TimeSpan baseDelay, int attempt, TimeSpan cap)
    {
        var ticks = baseDelay.Ticks;
        for (var i = 0; i < attempt && ticks < cap.Ticks; i++)
            ticks *= 2;
        return TimeSpan.FromTicks(Math.Min(ticks, cap.Ticks));
    }
}

/// <summary>
/// How long to wait before starting the file watcher again after it reported an error: 5 s, 10 s, 30 s, 1 min, then
/// 5 min; back to 5 s once it has been quiet for <see cref="QuietReset"/>.
/// </summary>
public sealed class WatcherRestartPolicy
{
    private static readonly TimeSpan[] Steps =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private int _attempt;
    private DateTimeOffset _last;

    public TimeSpan QuietReset { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How many restarts were asked for since the last quiet stretch.</summary>
    public int Restarts => _attempt;

    public TimeSpan Next(DateTimeOffset now)
    {
        if (_attempt > 0 && now - _last >= QuietReset)
            _attempt = 0;
        _last = now;
        return Steps[Math.Min(_attempt++, Steps.Length - 1)];
    }
}
