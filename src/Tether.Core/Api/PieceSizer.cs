namespace Tether.Core.Api;

/// <summary>
/// Chooses how big the next piece of an upload is. A proxy such as Cloudflare takes in a whole piece
/// before passing it on, so a broken connection costs the piece in flight: pieces aim at about
/// <see cref="Target"/> of sending at the speed the last pieces went, between <see cref="Min"/> and
/// <see cref="Max"/>, and are halved after a broken connection. Shared by all uploads of one client
/// (each transfer measures its own share of the bandwidth).
/// </summary>
internal sealed class PieceSizer
{
    /// <summary>How long one piece should take to send.</summary>
    public static readonly TimeSpan Target = TimeSpan.FromSeconds(30);

    public const long DefaultMin = 4L * 1024 * 1024;

    private readonly object _gate = new();
    private double? _bytesPerSecond;
    private long _next;

    public PieceSizer(long min, long max)
    {
        Max = Math.Max(1, max);
        Min = Math.Clamp(min, 1, Max);
        _next = Math.Clamp(Max / 4, Min, Max);
    }

    public long Min { get; }

    public long Max { get; }

    /// <summary>The size of the next piece.</summary>
    public long Next
    {
        get
        {
            lock (_gate)
                return _next;
        }
    }

    /// <summary>
    /// Whether a file of <paramref name="size"/> bytes goes in pieces: always above <see cref="Max"/>,
    /// and once the speed is known, whenever it would take longer than one piece should.
    /// </summary>
    public bool ShouldSplit(long size)
    {
        lock (_gate)
            return size > (_bytesPerSecond is null ? Max : _next);
    }

    /// <summary>A piece of <paramref name="bytes"/> went through in <paramref name="elapsed"/>.</summary>
    public void Succeeded(long bytes, TimeSpan elapsed)
    {
        if (bytes <= 0 || elapsed <= TimeSpan.Zero)
            return;
        var speed = bytes / elapsed.TotalSeconds;
        lock (_gate)
        {
            _bytesPerSecond = _bytesPerSecond is { } previous ? (previous + speed) / 2 : speed;
            var ideal = _bytesPerSecond.Value * Target.TotalSeconds;
            // Grow at most twofold per piece, so one fast moment does not make the next piece huge.
            _next = Math.Clamp((long)Math.Min(ideal, _next * 2.0), Min, Max);
        }
    }

    /// <summary>A piece was cut off: the next ones are half as big, so a bad connection loses less.</summary>
    public void Failed()
    {
        lock (_gate)
            _next = Math.Max(Min, _next / 2);
    }
}
