using System.Collections.ObjectModel;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The model behind the new nest picture: nine states, speeds, unreachable rules, and the cap of five computers.</summary>
public class NestMapModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
    private const string Me = "DESKTOP";

    private static DeviceInfo Dev(string name, bool online = true, double seenMinutesAgo = 0, string? icon = null) =>
        new(name, Now.AddDays(-30), Now.AddMinutes(-seenMinutesAgo), online, Icon: icon);

    private static StatusSnapshot Base(RunnerStatus status, params DeviceInfo[] devices) =>
        StatusSnapshot.Initial with { Status = status, Text = status.ToString(), LastSyncAt = Now, Server = Server, Devices = devices };

    private static StatusSnapshot WithPeer(StatusSnapshot s, string name, TransferReport report, double ageSeconds = 1) =>
        s with { Peers = new ReadOnlyDictionary<string, PeerActivity>(new Dictionary<string, PeerActivity>(s.Peers) { [name] = new PeerActivity(report, Now.AddSeconds(-ageSeconds), 0, null) }) };

    private static TransferReport Sending(double up, int files = 86) => new(up, 0, 0, files, 0, 1000, Uploading: true);
    private static TransferReport Getting(double down) => new(0, down, 0, 10, 0, 1000, Downloading: true);

    private static NestMapModel Map(StatusSnapshot s) => NestMapModel.Build(s, Me, Now);

    private static StatusSnapshot Normal(RunnerStatus status = RunnerStatus.Idle) =>
        Base(status, Dev(Me), Dev("LAPTOP"), Dev("MINI-PC"), Dev("OFFICE-PC", online: false, seenMinutesAgo: 90));

    // ---------------------------------------------------------------- the nine states

    [Fact]
    public void Syncing_ShowsOwnSpeedAndTheOthersGettingFiles()
    {
        var s = Normal(RunnerStatus.Syncing) with { Stage = SyncStage.Transferring, BytesPerSecond = 4.9 * 1024 * 1024, UpBytesPerSecond = 4.9 * 1024 * 1024, MovingUp = true };
        s = WithPeer(WithPeer(s, "LAPTOP", Getting(4.1 * 1024 * 1024)), "MINI-PC", Getting(2.6 * 1024 * 1024));
        var m = Map(s);

        Assert.Equal(NestMapState.Syncing, m.State);
        Assert.Equal(NestLineKind.Active, m.This.Kind);
        Assert.Equal("↑ 4.90 MB/s", m.This.Label);
        Assert.True(m.This.HasPill);
        Assert.Equal(NestDot.Ok, m.NestDot);
        Assert.Equal("412 GB free", m.NestSub);
        Assert.Equal(["LAPTOP", "MINI-PC", "OFFICE-PC"], m.Others.Select(o => o.Name));
        Assert.Equal((NestLineKind.Active, "↓ 4.10 MB/s", "getting them"), (m.Others[0].Kind, m.Others[0].Speed, m.Others[0].Status));
        Assert.Equal((NestLineKind.Active, "↓ 2.60 MB/s", "getting them"), (m.Others[1].Kind, m.Others[1].Speed, m.Others[1].Status));
        Assert.Equal((NestLineKind.Unreachable, "can’t reach", NestDot.Bad), (m.Others[2].Kind, m.Others[2].Status, m.Others[2].Dot));
        Assert.True(m.Others[2].HasCross);
    }

    [Fact]
    public void Syncing_WithADownloadShowsADownArrow_AndWithoutSpeedNoPill()
    {
        var down = Normal(RunnerStatus.Syncing) with { Stage = SyncStage.Transferring, BytesPerSecond = 1_000_000, DownBytesPerSecond = 1_000_000, MovingDown = true };
        Assert.StartsWith("↓ ", Map(down).This.Label);

        var checking = Normal(RunnerStatus.Syncing) with { Stage = SyncStage.Checking };
        var m = Map(checking);
        Assert.Equal(NestMapState.Syncing, m.State);
        Assert.Equal("checking", m.This.Label);
    }

    [Fact]
    public void UpToDate_IsGreenAndTheOthersAreInSync()
    {
        var m = Map(Normal());
        Assert.Equal(NestMapState.UpToDate, m.State);
        Assert.Equal((NestLineKind.Ok, "in sync", NestDot.Ok), (m.This.Kind, m.This.Label, m.This.Dot));
        Assert.All(m.Others.Where(o => o.Online), o => Assert.Equal((NestLineKind.Ok, "in sync"), (o.Kind, o.Status)));
        Assert.Equal(NestLineKind.Unreachable, m.Others.Single(o => o.Name == "OFFICE-PC").Kind);
    }

    [Fact]
    public void Waiting_IsNextForUsAndTheSenderShowsItsSpeed()
    {
        var s = Normal() with { WaitingFor = new PeerWait("LAPTOP", 86, 10) };
        s = WithPeer(s, "LAPTOP", Sending(3.8 * 1024 * 1024, 86));
        var m = Map(s);

        Assert.Equal(NestMapState.Waiting, m.State);
        Assert.Equal((NestLineKind.Next, "next"), (m.This.Kind, m.This.Label));
        var sender = m.Others.Single(o => o.Name == "LAPTOP");
        Assert.Equal((NestLineKind.Active, "↑ 3.80 MB/s", "sending 86 files"), (sender.Kind, sender.Speed, sender.Status));
        var other = m.Others.Single(o => o.Name == "MINI-PC");
        Assert.Equal((NestLineKind.Next, "next"), (other.Kind, other.Status));
    }

    [Fact]
    public void Stopped_IsAmberAndTheNestIsFine()
    {
        var m = Map(Normal(RunnerStatus.Blocked) with { BlockReason = BlockReason.MassDelete });
        Assert.Equal(NestMapState.Stopped, m.State);
        Assert.Equal((NestLineKind.Warn, "stopped", NestDot.Warn, NestDot.Ok), (m.This.Kind, m.This.Label, m.This.Dot, m.NestDot));
        Assert.Equal((NestLineKind.Idle, "online"), (m.Others[0].Kind, m.Others[0].Status));
        Assert.Equal(NestMapState.Stopped, Map(Normal(RunnerStatus.Blocked) with { BlockReason = BlockReason.FolderEmpty }).State);
    }

    [Fact]
    public void SignIn_IsAmberOnBothThisComputerAndTheNest()
    {
        var m = Map(Normal(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignInRequired });
        Assert.Equal(NestMapState.SignIn, m.State);
        Assert.Equal((NestLineKind.Warn, "sign in first", NestDot.Warn, NestDot.Warn), (m.This.Kind, m.This.Label, m.This.Dot, m.NestDot));
    }

    [Fact]
    public void SignedOut_IsRedAndTheNestSaysItRemovedThisOne()
    {
        var m = Map(Normal(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignedOut });
        Assert.Equal(NestMapState.SignedOut, m.State);
        Assert.Equal((NestLineKind.Bad, "signed out", NestDot.Bad, NestDot.Bad), (m.This.Kind, m.This.Label, m.This.Dot, m.NestDot));
        Assert.Equal("removed this one", m.NestSub);
        Assert.False(m.This.HasCross);
    }

    [Fact]
    public void Offline_OnlyThisLineIsUnreachable_TheOthersAreGreyAndSeen()
    {
        var s = Base(RunnerStatus.Offline, Dev(Me), Dev("LAPTOP", seenMinutesAgo: 15), Dev("MINI-PC", online: false, seenMinutesAgo: 20));
        var m = Map(s);

        Assert.Equal(NestMapState.Offline, m.State);
        Assert.Equal((NestLineKind.Unreachable, "can’t reach", NestDot.Off, NestDot.Bad), (m.This.Kind, m.This.Label, m.This.Dot, m.NestDot));
        Assert.True(m.This.HasCross);
        Assert.Equal("can’t reach it", m.NestSub);
        Assert.All(m.Others, o =>
        {
            Assert.Equal(NestLineKind.Next, o.Kind);
            Assert.False(o.HasCross);
            Assert.False(o.Online);
            Assert.Equal(NestDot.Off, o.Dot);
        });
        Assert.Equal("seen " + Now.AddMinutes(-15).ToLocalTime().ToString("HH:mm"), m.Others[0].Status);
        Assert.Equal("seen " + Now.AddMinutes(-20).ToLocalTime().ToString("HH:mm"), m.Others[1].Status);
    }

    [Fact]
    public void Paused_IsGreyDashed()
    {
        var m = Map(Normal(RunnerStatus.Paused) with { Paused = true });
        Assert.Equal(NestMapState.Paused, m.State);
        Assert.Equal((NestLineKind.Next, "paused", NestDot.Off, NestDot.Ok), (m.This.Kind, m.This.Label, m.This.Dot, m.NestDot));
        Assert.Equal(NestLineKind.Idle, m.Others[0].Kind);
    }

    [Theory]
    [InlineData(BlockReason.FolderMissing)]
    [InlineData(BlockReason.MarkerMissing)]
    [InlineData(BlockReason.MarkerMismatch)]
    public void FolderMissing_IsAmber(BlockReason reason)
    {
        var m = Map(Normal(RunnerStatus.Blocked) with { BlockReason = reason });
        Assert.Equal(NestMapState.FolderMissing, m.State);
        Assert.Equal((NestLineKind.Warn, "folder missing", NestDot.Warn, NestDot.Ok), (m.This.Kind, m.This.Label, m.This.Dot, m.NestDot));
    }

    // ---------------------------------------------------------------- the rules

    [Fact]
    public void SpeedsShowOnlyWhileTheOtherComputerReallyMoves()
    {
        var idleReport = WithPeer(Normal(), "LAPTOP", TransferReport.Idle);
        Assert.Null(Map(idleReport).Others.Single(o => o.Name == "LAPTOP").Speed);

        // A moving report that went stale (older than StatusSnapshot.LiveFor) is ignored.
        var stale = WithPeer(Normal(), "LAPTOP", Getting(5_000_000), ageSeconds: 60);
        var laptop = Map(stale).Others.Single(o => o.Name == "LAPTOP");
        Assert.Null(laptop.Speed);
        Assert.Equal(NestLineKind.Ok, laptop.Kind);

        var fresh = Map(WithPeer(Normal(), "LAPTOP", Getting(5_000_000))).Others.Single(o => o.Name == "LAPTOP");
        Assert.Equal("↓ 4.77 MB/s", fresh.Speed);
        Assert.True(fresh.HasPill);

        // Our own speed is never shown when not syncing.
        Assert.False(Map(Normal() with { BytesPerSecond = 5_000_000 }).This.HasPill);
    }

    [Fact]
    public void ADeviceThatIsNotOnlineIsUnreachable_UnlessItIsMovingFiles()
    {
        var s = Base(RunnerStatus.Idle, Dev(Me), Dev("LAPTOP", online: false, seenMinutesAgo: 5));
        var m = Map(s);
        Assert.Equal((NestLineKind.Unreachable, "can’t reach"), (m.Others[0].Kind, m.Others[0].Status));

        var m2 = Map(WithPeer(s, "LAPTOP", Getting(1_000_000)));
        Assert.Equal(NestLineKind.Active, m2.Others[0].Kind);
        Assert.True(m2.Others[0].Online);
    }

    [Fact]
    public void MoreThanFiveOthers_ShowsFiveOnlineAndMovingFirst_AndAMoreCount()
    {
        var devices = new List<DeviceInfo> { Dev(Me) };
        for (var i = 1; i <= 4; i++)
            devices.Add(Dev($"AWAY-{i}", online: false, seenMinutesAgo: i));
        for (var i = 1; i <= 3; i++)
            devices.Add(Dev($"ON-{i}", seenMinutesAgo: 10 + i));
        devices.Add(Dev("BUSY", seenMinutesAgo: 600));
        var s = WithPeer(Base(RunnerStatus.Idle, devices.ToArray()), "BUSY", Getting(1_000_000));
        var m = Map(s);

        Assert.Equal(8, 5 + m.MoreCount);
        Assert.Equal(5, m.Others.Count);
        Assert.Equal("+3 more", m.MoreText);
        Assert.Equal(["BUSY", "ON-1", "ON-2", "ON-3", "AWAY-1"], m.Others.Select(o => o.Name));

        Assert.Null(Map(Normal()).MoreText);
        Assert.Equal(0, Map(Normal()).MoreCount);
    }

    [Fact]
    public void ThisComputerAndLongGoneNamesAreNotListed()
    {
        var s = Base(RunnerStatus.Idle, Dev(Me), Dev("LAPTOP"), new DeviceInfo("OLD", Now.AddDays(-200), Now.AddDays(-90), false));
        var m = Map(s);
        Assert.Equal(["LAPTOP"], m.Others.Select(o => o.Name));
    }

    [Fact]
    public void NoDeviceListYet_GivesNoOtherLines()
    {
        var m = Map(StatusSnapshot.Initial with { Status = RunnerStatus.Idle });
        Assert.Empty(m.Others);
        Assert.Equal("online", m.NestSub);
    }

    // ---------------------------------------------------------------- icons on the lines

    [Fact]
    public void IconsAreTheChosenOnesWhenTheNestKnowsThem_AndGuessesOtherwise()
    {
        var s = Base(RunnerStatus.Idle, Dev(Me, icon: "office"), Dev("MY-LAPTOP"), Dev("TELLY", icon: "tv"), Dev("MACBOOK", icon: "mini"));
        var known = Map(s with { DeviceIconsSupported = true });
        Assert.Equal(DeviceIconKind.Office, known.This.Icon);
        Assert.Equal([DeviceIconKind.Laptop, DeviceIconKind.Tv, DeviceIconKind.Mini], known.Others.Select(o => o.Icon));

        var old = Map(s);
        Assert.Equal(DeviceIconKind.Desktop, old.This.Icon);
        Assert.Equal([DeviceIconKind.Laptop, DeviceIconKind.Desktop, DeviceIconKind.Laptop], old.Others.Select(o => o.Icon));
    }
}

public class DeviceIconTests
{
    [Theory]
    [InlineData("desktop", DeviceIconKind.Desktop)]
    [InlineData("laptop", DeviceIconKind.Laptop)]
    [InlineData("office", DeviceIconKind.Office)]
    [InlineData("mini", DeviceIconKind.Mini)]
    [InlineData("tv", DeviceIconKind.Tv)]
    [InlineData(" Laptop ", DeviceIconKind.Laptop)]
    public void ParsesTheServersValues(string value, DeviceIconKind expected)
    {
        Assert.True(DeviceIcon.TryParse(value, out var kind));
        Assert.Equal(expected, kind);
        Assert.Equal(DeviceIcon.ToWire(expected), value.Trim().ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("toaster")]
    [InlineData("laptop; drop table")]
    public void MissingOrUnknownValuesAreNotChosen(string? value)
    {
        Assert.False(DeviceIcon.TryParse(value, out var kind));
        Assert.Equal(DeviceIconKind.Desktop, kind);
        var device = new DeviceInfo("DESKTOP", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, Icon: value);
        Assert.Equal(DeviceIconKind.Desktop, DeviceIcon.For(device));
    }

    [Theory]
    [InlineData("LAPTOP-2", DeviceIconKind.Laptop)]
    [InlineData("my-laptop", DeviceIconKind.Laptop)]
    [InlineData("Alex's MacBook Pro", DeviceIconKind.Laptop)]
    [InlineData("Chromebook", DeviceIconKind.Laptop)]
    [InlineData("DESKTOP", DeviceIconKind.Desktop)]
    [InlineData("MINI-PC", DeviceIconKind.Desktop)]
    [InlineData("", DeviceIconKind.Desktop)]
    [InlineData(null, DeviceIconKind.Desktop)]
    public void GuessesFromTheNameWhenNothingIsChosen(string? name, DeviceIconKind expected) =>
        Assert.Equal(expected, DeviceIcon.Guess(name));

    [Fact]
    public void AnUnparsableChoiceFallsBackToTheGuess_AndAnOldNestAlwaysGuesses()
    {
        var chosen = new DeviceInfo("BOOK-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, Icon: "tv");
        Assert.Equal(DeviceIconKind.Tv, DeviceIcon.For(chosen));
        Assert.Equal(DeviceIconKind.Laptop, DeviceIcon.For(chosen, nestSupportsIcons: false));
        Assert.Equal(DeviceIconKind.Tv, DeviceIcon.For(chosen, nestSupportsIcons: true));
    }
}
