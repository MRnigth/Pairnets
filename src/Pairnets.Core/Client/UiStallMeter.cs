namespace Pairnets.Core.Client;

/// <summary>
/// Measures how long the window's thread is kept from answering: every half second a small job is posted to it and the
/// time until it runs is noted. <see cref="TakeLongestMs"/> (the health log asks once a minute) gives the longest wait since
/// last asked, counting a job still waiting. A spinning beachball shows up here as seconds, with the minute it happened.
/// </summary>
public sealed class UiStallMeter : IDisposable
{
    private readonly Action<Action> _postToUi;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private ITimer? _timer;
    private long _longestMs;
    private DateTimeOffset? _waitingSince;

    /// <param name="postToUi">Runs the given job on the window's thread, later.</param>
    public UiStallMeter(Action<Action> postToUi, TimeProvider? clock = null)
    {
        _postToUi = postToUi;
        _clock = clock ?? TimeProvider.System;
    }

    public void Start(TimeSpan? every = null)
    {
        var period = every ?? TimeSpan.FromMilliseconds(500);
        lock (_gate)
            _timer ??= _clock.CreateTimer(_ => Probe(), null, period, period);
    }

    /// <summary>Posts one probe unless the last one has not run yet.</summary>
    public void Probe()
    {
        DateTimeOffset sent;
        lock (_gate)
        {
            if (_waitingSince is not null)
                return; // the thread is still busy with the last one: that wait is what counts
            sent = _clock.GetUtcNow();
            _waitingSince = sent;
        }
        try
        {
            _postToUi(() =>
            {
                lock (_gate)
                {
                    var wait = (long)(_clock.GetUtcNow() - sent).TotalMilliseconds;
                    _longestMs = Math.Max(_longestMs, wait);
                    _waitingSince = null;
                }
            });
        }
        catch (Exception)
        {
            lock (_gate)
                _waitingSince = null; // the window's thread is gone (closing)
        }
    }

    /// <summary>The longest wait in milliseconds since the last call, a job still waiting included; starts counting again.</summary>
    public int TakeLongestMs()
    {
        lock (_gate)
        {
            var longest = _longestMs;
            if (_waitingSince is { } since)
                longest = Math.Max(longest, (long)(_clock.GetUtcNow() - since).TotalMilliseconds);
            _longestMs = 0;
            return (int)Math.Min(longest, int.MaxValue);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
