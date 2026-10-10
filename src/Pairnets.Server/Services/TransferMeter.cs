using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Pairnets.Server.Services;

/// <summary>
/// How fast each computer is uploading to and downloading from the server right now, counted from the bytes of its
/// file requests. Once a second <see cref="TransferBroadcaster"/> sends what changed to the apps ("PeerTransfer"),
/// so a computer can show the other one's real speed on its map instead of guessing.
/// </summary>
public sealed class TransferMeter
{
    /// <summary>A computer that stopped moving bytes is reported as finished after this long (no flicker between small files).</summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(2);

    /// <summary>One computer's speeds; <see cref="Uploading"/> and <see cref="Downloading"/> are false in the last report.</summary>
    public sealed record Report(string Device, long UpBytesPerSecond, long DownBytesPerSecond, bool Uploading, bool Downloading)
    {
        public bool Active => Uploading || Downloading;
    }

    internal sealed class Meter
    {
        public long Up;
        public long Down;
        public int Uploads;
        public int Downloads;

        // Only touched by Tick (one caller at a time).
        public long SeenUp;
        public long SeenDown;
        public long PreviousUp;
        public long PreviousDown;
        public double PreviousSeconds;
        public DateTimeOffset? LastTick;
        public DateTimeOffset LastBusy;
        public Report? Last;
    }

    private readonly ConcurrentDictionary<string, Meter> _meters = new(StringComparer.Ordinal);
    private readonly object _tickGate = new();

    /// <summary>A file request of <paramref name="device"/> starts; dispose the result when it ends.</summary>
    public Transfer Begin(string device, bool upload)
    {
        var meter = _meters.GetOrAdd(device, _ => new Meter());
        if (upload)
            Interlocked.Increment(ref meter.Uploads);
        else
            Interlocked.Increment(ref meter.Downloads);
        return new Transfer(meter, upload);
    }

    /// <summary>One file request: counts its bytes and ends on dispose.</summary>
    public sealed class Transfer : IDisposable
    {
        private readonly Meter _meter;
        private readonly bool _upload;
        private int _ended;

        internal Transfer(Meter meter, bool upload) => (_meter, _upload) = (meter, upload);

        public void Count(long bytes)
        {
            if (bytes <= 0)
                return;
            if (_upload)
                Interlocked.Add(ref _meter.Up, bytes);
            else
                Interlocked.Add(ref _meter.Down, bytes);
        }

        /// <summary><paramref name="inner"/> with every byte read through it counted.</summary>
        public Stream Wrap(Stream inner) => new CountingStream(inner, Count);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 1)
                return;
            if (_upload)
                Interlocked.Decrement(ref _meter.Uploads);
            else
                Interlocked.Decrement(ref _meter.Downloads);
        }
    }

    /// <summary>
    /// Called about once a second: the reports to send. A busy computer is reported every time (its speed changes);
    /// one that went quiet gets one last report with both flags false, then nothing until it starts again.
    /// </summary>
    public IReadOnlyList<Report> Tick(DateTimeOffset now)
    {
        var reports = new List<Report>();
        lock (_tickGate)
        {
            foreach (var (device, m) in _meters)
            {
                var up = Interlocked.Read(ref m.Up);
                var down = Interlocked.Read(ref m.Down);
                var deltaUp = up - m.SeenUp;
                var deltaDown = down - m.SeenDown;
                (m.SeenUp, m.SeenDown) = (up, down);
                var seconds = m.LastTick is { } last ? Math.Max(0.001, (now - last).TotalSeconds) : 1;
                m.LastTick = now;
                // Over the last two ticks, so one slow second doesn't make the number jump.
                var span = seconds + m.PreviousSeconds;
                var upRate = (long)((deltaUp + m.PreviousUp) / span);
                var downRate = (long)((deltaDown + m.PreviousDown) / span);
                (m.PreviousUp, m.PreviousDown, m.PreviousSeconds) = (deltaUp, deltaDown, seconds);

                var uploading = Volatile.Read(ref m.Uploads) > 0 || deltaUp > 0;
                var downloading = Volatile.Read(ref m.Downloads) > 0 || deltaDown > 0;
                if (uploading || downloading)
                {
                    m.LastBusy = now;
                    m.Last = new Report(device, upRate, downRate, uploading, downloading);
                    reports.Add(m.Last);
                }
                else if (m.Last is { Active: true } previous)
                {
                    // Between two small files: keep the last direction a moment, the speed falls as it really does.
                    m.Last = now - m.LastBusy >= QuietAfter
                        ? new Report(device, 0, 0, false, false)
                        : previous with { UpBytesPerSecond = upRate, DownBytesPerSecond = downRate };
                    reports.Add(m.Last);
                }
            }
        }
        return reports;
    }

    /// <summary>The computers moving files right now (sent to an app that just connected).</summary>
    public IReadOnlyList<Report> Current()
    {
        lock (_tickGate)
            return _meters.Values.Select(m => m.Last).OfType<Report>().Where(r => r.Active).ToList();
    }

    /// <summary>A read-only view of a stream that counts the bytes read (seeking is passed through, for range requests).</summary>
    private sealed class CountingStream(Stream inner, Action<long> count) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count1)
        {
            var n = inner.Read(buffer, offset, count1);
            count(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            count(n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count1, CancellationToken cancellationToken)
        {
            var n = await inner.ReadAsync(buffer.AsMemory(offset, count1), cancellationToken).ConfigureAwait(false);
            count(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            count(n);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count1) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>Sends <see cref="TransferMeter"/>'s reports to the apps once a second.</summary>
public sealed class TransferBroadcaster(TransferMeter meter, IHubContext<Web.SyncHub> hub, ILogger<TransferBroadcaster> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var r in meter.Tick(DateTimeOffset.UtcNow))
                {
                    try
                    {
                        await hub.Clients.All.SendAsync(Web.SyncHub.PeerTransferMethod,
                            r.Device, r.UpBytesPerSecond, r.DownBytesPerSecond, r.Uploading, r.Downloading, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogDebug("Could not send transfer speeds: {Error}", ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
