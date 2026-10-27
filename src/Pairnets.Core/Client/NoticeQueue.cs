using System.Threading.Channels;

namespace Pairnets.Core.Client;

/// <summary>
/// Shows the computer's notices one at a time, at most <c>perWindow</c> in a minute. A big first sync can raise
/// thousands of notices (one per file that can't sync, one per conflict); the Mac shows each by starting a program, and
/// thousands of those at once can use up the whole account's process limit. What is over the limit is counted and said
/// once, as one notice, when the minute is over.
/// </summary>
public sealed class NoticeQueue : IDisposable
{
    private readonly Action<string, string, Action?> _show;
    private readonly TimeProvider _clock;
    private readonly int _perWindow;
    private readonly TimeSpan _window;
    private readonly Channel<(string Title, string Text, Action? OnClick)> _queue = Channel.CreateBounded<(string, string, Action?)>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _shown = new();
    private readonly Task _worker;
    private ITimer? _summaryTimer;
    private int _suppressed;

    public NoticeQueue(Action<string, string> show, int perWindow = 3, TimeSpan? window = null, TimeProvider? clock = null)
        : this((title, text, _) => show(title, text), perWindow, window, clock)
    {
    }

    /// <summary>As above, and each notice carries what a click on it does (null: the shower's default).</summary>
    public NoticeQueue(Action<string, string, Action?> show, int perWindow = 3, TimeSpan? window = null, TimeProvider? clock = null)
    {
        _show = show;
        _perWindow = perWindow;
        _window = window ?? TimeSpan.FromMinutes(1);
        _clock = clock ?? TimeProvider.System;
        _worker = Task.Run(Work);
    }

    /// <summary>How many notices were left out since the last summary.</summary>
    public int Suppressed
    {
        get
        {
            lock (_gate)
                return _suppressed;
        }
    }

    /// <summary>How many notices wait to be shown.</summary>
    public int Waiting => _queue.Reader.Count;

    public void Add(string title, string text, Action? onClick = null)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            while (_shown.Count > 0 && now - _shown.Peek() >= _window)
                _shown.Dequeue();
            if (_shown.Count >= _perWindow || !_queue.Writer.TryWrite((title, text, onClick)))
            {
                _suppressed++;
                _summaryTimer ??= _clock.CreateTimer(_ => Summarize(), null, _window, Timeout.InfiniteTimeSpan);
                return;
            }
            _shown.Enqueue(now);
        }
    }

    private void Summarize()
    {
        int count;
        lock (_gate)
        {
            _summaryTimer?.Dispose();
            _summaryTimer = null;
            count = _suppressed;
            _suppressed = 0;
            if (count == 0)
                return;
            _shown.Enqueue(_clock.GetUtcNow());
        }
        _queue.Writer.TryWrite(("Pairnets", count == 1
            ? "1 more notice was held back. Open Pairnets to see everything that needs you."
            : $"{count} more notices were held back. Open Pairnets to see everything that needs you.", null));
    }

    private async Task Work()
    {
        await foreach (var (title, text, onClick) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _show(title, text, onClick);
            }
            catch (Exception)
            {
                // A notice that can't be shown is not worth an error.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _summaryTimer?.Dispose();
            _summaryTimer = null;
        }
        _queue.Writer.TryComplete();
    }
}
