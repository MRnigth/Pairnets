using System.Diagnostics;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

public class SpeedAndLimitTests
{
    [Fact]
    public async Task NoLimitNeverWaits()
    {
        var t = new Throttle();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
            await t.WaitAsync(1024 * 1024, default);
        Assert.True(sw.ElapsedMilliseconds < 500);
    }

    [Fact]
    public async Task LimitIsSharedByParallelTransfers()
    {
        // On a clock of its own, so a slow computer cannot change the answer: the time is the throttle's, not the CPU's.
        // Alone, 1 MB at 2 MB/s takes half a second. Four transfers of 1 MB each (256 KB chunks) share the limit, so
        // all four together take 4 MB / 2 MB/s = 2 s, not the half second each would take with a limit of its own.
        Assert.Equal(0.5, await VirtualClock.TransfersTakeAsync(transfers: 1), 3);
        Assert.Equal(2.0, await VirtualClock.TransfersTakeAsync(transfers: 4), 3);
    }

    [Fact]
    public void LimitHasAFloorAndZeroMeansNone()
    {
        var t = new Throttle();
        t.SetMegabytesPerSecond(0.001);
        Assert.Equal(Throttle.MinimumBytesPerSecond, t.BytesPerSecond);
        t.SetMegabytesPerSecond(0);
        Assert.Null(t.BytesPerSecond);
        t.SetMegabytesPerSecond(null);
        Assert.Null(t.BytesPerSecond);
    }

    [Fact]
    public void SpeedDurationAndLimitTexts()
    {
        Assert.Equal("12.4 MB/s", Format.Speed(12.4 * 1024 * 1024));
        Assert.Equal("less than a minute", Format.Duration(TimeSpan.FromSeconds(20)));
        Assert.Equal("about 2 min", Format.Duration(TimeSpan.FromSeconds(95)));
        Assert.Equal("about 1 h 20 min", Format.Duration(TimeSpan.FromMinutes(80)));
        Assert.Equal("about 2 h", Format.Duration(TimeSpan.FromMinutes(120)));

        Assert.Null(new ClientSettings().LimitText);
        Assert.Equal("Limited to 5 MB/s", new ClientSettings { UploadLimitMBps = 5, DownloadLimitMBps = 5 }.LimitText);
        Assert.Equal("Limited to 5 MB/s up", new ClientSettings { UploadLimitMBps = 5 }.LimitText);
        Assert.Equal("Limited to 0.5 MB/s up, 10 MB/s down", new ClientSettings { UploadLimitMBps = 0.5, DownloadLimitMBps = 10 }.LimitText);
        Assert.Equal(4, new ClientSettings { ParallelTransfers = 3 }.EffectiveParallelTransfers);
        Assert.Equal(8, new ClientSettings { ParallelTransfers = 8 }.EffectiveParallelTransfers);
    }

    [Fact]
    public void SnapshotSpeedOverallAndServerSpace()
    {
        var s = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing,
            FilesDone = 37,
            FilesTotal = 120,
            PassBytesDone = 412L * 1024 * 1024,
            PassBytesTotal = 1331L * 1024 * 1024,
            BytesPerSecond = 12.4 * 1024 * 1024,
            Active = [new ActiveTransfer("Photos/a.jpg", "upload", 78, 100), new ActiveTransfer("b.jpg", "upload", 41, 100)],
            Operation = "upload",
            CurrentPath = "b.jpg",
        };
        Assert.Equal("Uploading 120 files", s.BatchTitle);
        Assert.Equal("12.4 MB/s · about 2 min left", s.SpeedText);
        Assert.Equal("37 of 120 files · 412 MB of 1.30 GB", s.OverallText);
        Assert.Equal(30, s.OverallPercent);
        Assert.Equal("78%", s.Active[0].PercentText);
        Assert.Equal("a.jpg", s.Active[0].FileName);

        Assert.Null(s.ServerFreeText);
        var big = s with { Server = new ServerInfo("id", 1, 1, "1.0.5", 412L << 30, 1L << 40) };
        Assert.Equal("412 GB free on server", big.ServerFreeText);
        Assert.False(big.ServerSpaceLow);
        Assert.True((s with { Server = new ServerInfo("id", 1, 1, "1.0.5", 3L << 30, 100L << 30) }).ServerSpaceLow);
        Assert.True((s with { Server = new ServerInfo("id", 1, 1, "1.0.5", 40L << 30, 1L << 40) }).ServerSpaceLow);
    }

    /// <summary>
    /// A clock that moves only when every transfer is waiting on one of its timers: then it jumps to the earliest one
    /// and fires it. So the time a run takes is exactly what the throttle asked for, however slow the computer is.
    /// </summary>
    private sealed class VirtualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            lock (_gate)
                return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, () => callback(state));
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Seconds on this clock until <paramref name="transfers"/> parallel 1 MB transfers under a 2 MB/s limit are done.</summary>
        public static async Task<double> TransfersTakeAsync(int transfers)
        {
            var clock = new VirtualClock();
            var t = new Throttle(clock);
            t.SetMegabytesPerSecond(2);
            var running = transfers;
            var finishedAt = 0L;
            var all = Task.WhenAll(Enumerable.Range(0, transfers).Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 4; i++)
                    await t.WaitAsync(256 * 1024, default);
                lock (clock._gate)
                    finishedAt = Math.Max(finishedAt, clock._now);
                Interlocked.Decrement(ref running);
            })));
            var deadline = DateTime.UtcNow.AddSeconds(30); // (real time: only a hang trips this)
            while (Volatile.Read(ref running) > 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "the transfers never finished");
                Timer? next;
                lock (clock._gate)
                {
                    // Move on only when every transfer still running waits on a timer.
                    next = clock._timers.Count >= Volatile.Read(ref running) && clock._timers.Count > 0 ? clock._timers.MinBy(x => x.Due) : null;
                    if (next is not null)
                    {
                        clock._timers.Remove(next);
                        clock._now = Math.Max(clock._now, next.Due);
                    }
                }
                if (next is null)
                    await Task.Delay(1);
                else
                    next.Fire();
            }
            await all;
            return (double)finishedAt / TimeSpan.TicksPerSecond;
        }

        private sealed class Timer(VirtualClock clock, Action fire) : ITimer
        {
            public long Due { get; private set; }

            public void Fire() => fire();

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                        return true;
                    Due = clock._now + dueTime.Ticks;
                    clock._timers.Add(this);
                }
                return true;
            }

            public void Dispose()
            {
                lock (clock._gate)
                    clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
