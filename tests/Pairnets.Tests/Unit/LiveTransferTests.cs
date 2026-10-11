using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>
/// Animations that show what really moves: the badge's motion and the map's lines, from each computer's own reports.
/// </summary>
public class LiveTransferTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);

    private static StatusSnapshot Syncing(params ActiveTransfer[] active) => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Syncing,
        Text = "Syncing",
        LastSyncAt = Now,
        Server = Server,
        Devices = [new DeviceInfo("MAC", Now.AddDays(-30), Now, true), new DeviceInfo("DESKTOP", Now.AddDays(-30), Now, true)],
        Active = active,
        CurrentPath = active.Length > 0 ? active[0].Path : null,
        Operation = active.Length > 0 ? active[0].Operation : null,
    };

    [Fact]
    public void TheBadgeShowsWhatReallyMoves()
    {
        Assert.Equal(SyncMotion.Checking, Syncing().Motion);
        Assert.Equal("Syncing…", Syncing().Headline);
        Assert.Equal("Deleting files", (Syncing() with { CurrentPath = "old.txt", Operation = "delete" }).Headline);

        var up = Syncing(new ActiveTransfer("a", "upload", 1, 10));
        Assert.Equal(SyncMotion.Up, up.Motion);
        Assert.Equal("Uploading", up.Headline);
        Assert.Equal(string.Empty, up.DetailText); // not "Syncing" under it

        var down = Syncing(new ActiveTransfer("b", "download", 1, 10));
        Assert.Equal(SyncMotion.Down, down.Motion);
        Assert.Equal("Downloading", down.Headline);

        var both = Syncing(new ActiveTransfer("a", "upload", 1, 10), new ActiveTransfer("b", "download", 1, 10));
        Assert.Equal(SyncMotion.Both, both.Motion);

        // Between two files nothing is listed for a moment; the session keeps the direction, so nothing flickers.
        Assert.Equal(SyncMotion.Up, (Syncing() with { MovingUp = true }).Motion);

        var stalled = up with { StalledSince = Now.AddSeconds(-12) };
        Assert.Equal(SyncMotion.Stalled, stalled.Motion);
        Assert.Equal("Waiting for the network", stalled.Headline);
        Assert.Equal("Stalled · nothing moved for 12 s", stalled.StallText(Now));
        Assert.Equal(string.Empty, up.StallText(Now));
        Assert.NotEmpty(stalled.DetailText);

        Assert.Equal(SyncMotion.None, (up with { Status = RunnerStatus.Idle }).Motion);
        Assert.Equal(SyncMotion.None, (Syncing() with { WaitingFor = new PeerWait("DESKTOP", 300, 2) }).Motion);
    }

    [Fact]
    public void TheLineToTheOtherComputerShowsOnlyWhatTheServerReports()
    {
        // Uploading here no longer makes the other computer look like it downloads.
        var up = Syncing(new ActiveTransfer("a", "upload", 1, 10)) with { UpBytesPerSecond = 5 << 20 };
        var map = DeviceMap.Build(up, "MAC", Now);
        Assert.Equal(LinkFlow.Up, map.HereFlow);
        Assert.Equal(LinkFlow.None, map.OtherFlow);
        Assert.Equal("↑ 5.00 MB/s", map.HereRateText);
        Assert.Null(map.OtherRateText);
        Assert.Equal("Online", map.Other.Detail);

        // The server says DESKTOP downloads this computer's change.
        var reported = up with
        {
            MyChangeVersion = 42,
            Peers = new Dictionary<string, PeerActivity>(StringComparer.OrdinalIgnoreCase)
            {
                ["DESKTOP"] = new(new TransferReport(0, 3 << 20, 0, 0, 0, 0, Downloading: true), Now.AddSeconds(-1), 40, Now.AddMinutes(-5)),
            },
        };
        map = DeviceMap.Build(reported, "MAC", Now);
        Assert.Equal(LinkFlow.Down, map.OtherFlow);
        Assert.Equal(PeerState.Downloading, map.OtherState);
        Assert.Equal("Downloading your changes", map.Other.Detail);
        Assert.Equal("↓ 3.00 MB/s", map.OtherRateText);
        Assert.Equal(3.0 * (1 << 20), map.OtherDownSpeed!.Value);

        // A report that stopped coming (the server went away) no longer counts.
        Assert.Equal(LinkFlow.None, DeviceMap.Build(reported, "MAC", Now + StatusSnapshot.LiveFor + TimeSpan.FromSeconds(1)).OtherFlow);

        // It finished and has the change: a check mark for a minute.
        var done = reported with
        {
            Status = RunnerStatus.Idle,
            Active = [],
            Peers = new Dictionary<string, PeerActivity> { ["DESKTOP"] = new(TransferReport.Idle, Now, 42, Now.AddSeconds(-3)) },
        };
        map = DeviceMap.Build(done, "MAC", Now);
        Assert.Equal(PeerState.HasChanges, map.OtherState);
        Assert.Equal("Has your changes", map.Other.Detail);
        Assert.Equal(LinkFlow.None, map.OtherFlow);
        Assert.Equal(PeerState.None, DeviceMap.Build(done, "MAC", Now + DeviceMap.HasChangesWindow + TimeSpan.FromSeconds(4)).OtherState);
        Assert.Equal(PeerState.None, DeviceMap.Build(done with { MyChangeVersion = 43 }, "MAC", Now).OtherState); // a newer change it hasn't got

        // Sending, reported by the server even before a file lands.
        var sending = done with { Peers = new Dictionary<string, PeerActivity> { ["DESKTOP"] = new(new TransferReport(800_000, 0, 0, 0, 0, 0, Uploading: true), Now, 42, null) } };
        map = DeviceMap.Build(sending, "MAC", Now);
        Assert.Equal(LinkFlow.Up, map.OtherFlow);
        Assert.Equal("Sending changes", map.Other.Detail);
        Assert.Equal("↑ 781 KB/s", map.OtherRateText);
    }

    [Fact]
    public void StalledAndTwoWayLinesSayHowFast()
    {
        var both = Syncing(new ActiveTransfer("a", "upload", 1, 10), new ActiveTransfer("b", "download", 1, 10))
            with { UpBytesPerSecond = 3 << 20, DownBytesPerSecond = 2 << 20 };
        var map = DeviceMap.Build(both, "MAC", Now);
        Assert.Equal(LinkFlow.Both, map.HereFlow);
        Assert.Equal("↑ 3.00 MB/s · ↓ 2.00 MB/s", map.HereRateText);

        var stalled = Syncing(new ActiveTransfer("a", "upload", 1, 10)) with { StalledSince = Now.AddSeconds(-6) };
        map = DeviceMap.Build(stalled, "MAC", Now);
        Assert.True(map.HereStalled);
        Assert.Equal("↑ 0 B/s", map.HereRateText);

        // Nothing moved yet: no number at all.
        Assert.Null(DeviceMap.Build(Syncing(new ActiveTransfer("a", "upload", 0, 10)), "MAC", Now).HereRateText);
    }

    [Fact]
    public void DotsGoFasterWithMoreMegabytes()
    {
        Assert.Equal(0, DeviceMap.DotSpeed(0));
        Assert.Equal(DeviceMap.DefaultDotSpeed, DeviceMap.DotSpeed(null)); // speed unknown: the old steady pace
        var slow = DeviceMap.DotSpeed(100_000);
        var medium = DeviceMap.DotSpeed(5 << 20);
        var fast = DeviceMap.DotSpeed(50 << 20);
        Assert.True(slow > 0 && slow < medium && medium < fast);
        Assert.Equal(175, DeviceMap.DotSpeed(10L << 30)); // with a cap
    }

    [Fact]
    public void APassThatLeftNothingBehindIsCaughtUp()
    {
        Assert.True(new PassResult { ServerVersion = 7 }.CaughtUp);
        Assert.False(new PassResult { ServerVersion = 7, Errors = 1 }.CaughtUp);
        Assert.False(new PassResult { ServerVersion = 7, HeldDownloads = 3 }.CaughtUp);
        Assert.False(new PassResult { ServerVersion = 7, Deferred = 1 }.CaughtUp);
        Assert.False(new PassResult { ServerVersion = 7, Outcome = PassOutcome.Offline }.CaughtUp);
        Assert.False(new PassResult().CaughtUp);
    }
}
