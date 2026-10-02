namespace Tether.Core.Api;

/// <summary>
/// A speed limit shared by every transfer in one direction (all parallel uploads together stay
/// under the upload limit). Token bucket with a half-second burst; callers that go over the limit
/// borrow from the future and wait, so concurrent transfers are served in arrival order.
/// </summary>
public sealed class Throttle(TimeProvider? clock = null)
{
    /// <summary>Lowest limit accepted (100 KB/s); lower values would trip the 60 s stall watchdog.</summary>
    public const long MinimumBytesPerSecond = 100 * 1024;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private long? _rate;
    private double _tokens;
    private long _lastTicks;

    /// <summary>Bytes per second, or null for no limit. Can be changed while transfers run.</summary>
    public long? BytesPerSecond
    {
        get
        {
            lock (_gate)
                return _rate;
        }
        set
        {
            lock (_gate)
            {
                _rate = value is null or <= 0 ? null : Math.Max(value.Value, MinimumBytesPerSecond);
                _tokens = 0;
                _lastTicks = _clock.GetTimestamp();
            }
        }
    }

    /// <summary>Sets the limit from megabytes per second (null or 0 = no limit).</summary>
    public void SetMegabytesPerSecond(double? mbps) =>
        BytesPerSecond = mbps is null or <= 0 ? null : (long)(mbps.Value * 1024 * 1024);

    /// <summary>Waits until <paramref name="bytes"/> may be sent or received under the limit.</summary>
    public Task WaitAsync(int bytes, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_rate is not { } rate)
                return Task.CompletedTask;
            var now = _clock.GetTimestamp();
            var elapsed = _clock.GetElapsedTime(_lastTicks, now).TotalSeconds;
            _lastTicks = now;
            _tokens = Math.Min(_tokens + elapsed * rate, rate * 0.5);
            _tokens -= bytes;
            if (_tokens >= 0)
                return Task.CompletedTask;
            wait = TimeSpan.FromSeconds(-_tokens / rate);
        }
        return Task.Delay(wait, _clock, ct);
    }
}
