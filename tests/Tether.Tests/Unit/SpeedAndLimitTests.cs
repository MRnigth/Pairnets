using System.Diagnostics;
using Tether.Core;
using Tether.Core.Api;
using Tether.Core.Client;
using Tether.Core.Settings;
using Tether.Core.Sync;

namespace Tether.Tests.Unit;

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
        var t = new Throttle();
        t.SetMegabytesPerSecond(2);
        var sw = Stopwatch.StartNew();
        // Four "transfers" of 1 MB each in 256 KB chunks: 4 MB at 2 MB/s, minus the half-second burst.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            for (var i = 0; i < 4; i++)
                await t.WaitAsync(256 * 1024, default);
        }));
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.3, 3.5);
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
}
