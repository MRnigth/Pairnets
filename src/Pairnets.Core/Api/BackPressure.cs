namespace Pairnets.Core.Api;

/// <summary>
/// What an app does when the server, or the Pairnets service in front of it, answers 429 "too many requests": slow down,
/// not fail. Every request of this client then waits until the time the answer named (Retry-After; without one 5 s,
/// doubling up to a minute while the 429s go on), the refused request is sent again, and afterwards only one request runs
/// at a time. That limit doubles every <see cref="RecoverStep"/> without another 429 until it is gone. So a big first
/// sync of many small files backs off as one, instead of every transfer hammering on and failing.
/// </summary>
public sealed class BackPressure
{
    /// <summary>The wait after a 429 without Retry-After (doubled for each one in a row, up to <see cref="MaxDefaultWait"/>).</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxDefaultWait = TimeSpan.FromMinutes(1);

    /// <summary>The longest wait believed from a Retry-After.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>How long the lowered limit holds before it doubles.</summary>
    public static readonly TimeSpan RecoverStep = TimeSpan.FromSeconds(15);

    /// <summary>Above this many requests at once the limit is dropped altogether.</summary>
    private const int LiftAbove = 32;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<TaskCompletionSource> _waiting = [];
    private DateTimeOffset _until = DateTimeOffset.MinValue;
    private DateTimeOffset _limitSince;
    private int _limit = int.MaxValue;
    private int _inFlight;
    private int _strikes;
    private ITimer? _resumeTimer;

    public BackPressure(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Raised with the time requests go on again when a 429 arrives, and with null when that time has come. May be
    /// raised on any thread.
    /// </summary>
    public event Action<DateTimeOffset?>? Changed;

    /// <summary>When requests go on again, or null when they are not held.</summary>
    public DateTimeOffset? HeldUntil
    {
        get
        {
            lock (_gate)
                return _until > _clock.GetUtcNow() ? _until : null;
        }
    }

    /// <summary>How many requests may run at once right now (<see cref="int.MaxValue"/>: no limit).</summary>
    public int Limit
    {
        get
        {
            lock (_gate)
            {
                Recover(_clock.GetUtcNow());
                return _limit;
            }
        }
    }

    /// <summary>
    /// Waits until a request may go: after a held time, and while fewer than <see cref="Limit"/> run. <paramref name="beforeWaiting"/>
    /// runs once, just before the first real wait (the caller pauses its own timeout then). Dispose the slot when the answer arrived.
    /// </summary>
    public async ValueTask<Slot> EnterAsync(CancellationToken ct, Action? beforeWaiting = null)
    {
        var waited = false;
        while (true)
        {
            TimeSpan hold;
            TaskCompletionSource? turn = null;
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                Recover(now);
                hold = _until - now;
                if (hold <= TimeSpan.Zero)
                {
                    if (_inFlight < _limit)
                    {
                        _inFlight++;
                        return new Slot(this, waited);
                    }
                    turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiting.Add(turn);
                }
            }

            if (!waited)
            {
                waited = true;
                beforeWaiting?.Invoke();
            }
            if (turn is null)
            {
                await Task.Delay(hold, _clock, ct).ConfigureAwait(false);
                continue;
            }
            using (ct.Register(() => turn.TrySetCanceled(ct)))
            {
                try
                {
                    await turn.Task.ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                        _waiting.Remove(turn);
                }
            }
        }
    }

    /// <summary>A 429 arrived: hold every request for <paramref name="retryAfter"/> (or the default), then one at a time.</summary>
    public void Refused(TimeSpan? retryAfter)
    {
        DateTimeOffset until;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            _strikes++;
            var wait = retryAfter is { } named && named > TimeSpan.Zero
                ? (named > MaxWait ? MaxWait : named)
                : TimeSpan.FromTicks(Math.Min(MaxDefaultWait.Ticks, DefaultWait.Ticks << Math.Min(_strikes - 1, 8)));
            if (wait < TimeSpan.FromSeconds(1))
                wait = TimeSpan.FromSeconds(1);
            if (now + wait > _until)
                _until = now + wait;
            until = _until;
            _limit = 1;
            _limitSince = until;
            _resumeTimer?.Dispose();
            _resumeTimer = _clock.CreateTimer(_ => Resumed(), null, until - now, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke(until);
    }

    /// <summary>An answer that was not a 429: the next 429 starts again from the short default wait.</summary>
    public void Answered()
    {
        lock (_gate)
            _strikes = 0;
    }

    private void Resumed()
    {
        lock (_gate)
        {
            if (_until > _clock.GetUtcNow())
                return; // another 429 moved it on; its own timer reports that one
            WakeAll();
        }
        Changed?.Invoke(null);
    }

    /// <summary>Doubles a lowered limit for every quiet <see cref="RecoverStep"/>. Call with the lock held.</summary>
    private void Recover(DateTimeOffset now)
    {
        if (_limit == int.MaxValue || now < _limitSince + RecoverStep)
            return;
        while (_limit != int.MaxValue && now >= _limitSince + RecoverStep)
        {
            _limit = _limit >= LiftAbove ? int.MaxValue : _limit * 2;
            _limitSince += RecoverStep;
        }
        WakeAll();
    }

    /// <summary>Lets every queued request look again. Call with the lock held.</summary>
    private void WakeAll()
    {
        foreach (var turn in _waiting)
            turn.TrySetResult();
        _waiting.Clear();
    }

    private void Leave()
    {
        lock (_gate)
        {
            _inFlight--;
            WakeAll();
        }
    }

    /// <summary>One request's place; <see cref="Waited"/> says whether it had to wait for it.</summary>
    public readonly struct Slot(BackPressure owner, bool waited) : IDisposable
    {
        public bool Waited { get; } = waited;

        public void Dispose() => owner?.Leave();
    }
}
