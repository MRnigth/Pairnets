using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The left side (open and folded): every state of the design's Sidebar, the other computers and the tiles' notes.</summary>
public class SidePanelModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private const double MB = 1 << 20;

    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.48", 412L << 30, 1L << 40);

    private static DeviceInfo Device(string name, bool online, TimeSpan? seenAgo = null) =>
        new(name, Now.AddDays(-30), Now - (seenAgo ?? TimeSpan.Zero), online, "1.0.48", "Windows");

    private static IReadOnlyList<DeviceInfo> Six() =>
    [
        Device("DESKTOP", true),
        Device("LAPTOP", true),
        Device("MINI-PC", true),
        Device("MEDIA-PC", false, TimeSpan.FromDays(2)),
        Device("OFFICE-PC", false, TimeSpan.FromDays(3)),
        Device("KITCHEN-PC", false, TimeSpan.FromDays(4)),
    ];

    private static StatusSnapshot Base(RunnerStatus status = RunnerStatus.Idle) =>
        StatusSnapshot.Initial with { Status = status, Text = "x", LastSyncAt = Now, Server = Server, Devices = Six() };

    private static Dictionary<string, PeerActivity> Peers(double laptopDown, double miniDown = 0, double laptopUp = 0) => new()
    {
        ["LAPTOP"] = new(new TransferReport(laptopUp, laptopDown, 3, 30, 0, 0), Now, 0, null),
        ["MINI-PC"] = new(miniDown > 0 ? new TransferReport(0, miniDown, 1, 10, 0, 0) : null, Now, 0, null),
    };

    private static SidePanelModel Build(StatusSnapshot s, int activity = 37, int attention = 3, SideUpdate? update = null) =>
        SidePanelModel.Build(s, "DESKTOP", activity, attention, update, Now);

    private static StatusSnapshot SyncingNow() => Base(RunnerStatus.Syncing) with
    {
        Stage = SyncStage.Transferring, CurrentPath = "a/b.mp4", Operation = "upload", FilesDone = 37, FilesTotal = 120,
        PassBytesDone = 31, PassBytesTotal = 100, BytesPerSecond = 4.9 * MB, UpBytesPerSecond = 4.9 * MB,
        Active = [new ActiveTransfer("a/b.mp4", "upload", 10, 100)],
        Peers = Peers(4.1 * MB, 2.6 * MB),
    };

    // ------------------------------------------------------------------ this computer and the nest, per state

    [Fact]
    public void SyncingShowsTheBarTheSpeedAndTheLiveComputers()
    {
        var m = Build(SyncingNow());
        Assert.Equal("This one · sending 4.90 MB/s", m.This.Text);
        Assert.Equal(SideTone.Accent, m.This.Tone);
        Assert.True(m.This.Highlighted);
        Assert.Equal(SideDot.Ok, m.This.Dot);
        Assert.Equal(31, m.This.BarPercent);
        Assert.Equal(31, m.RingPercent);
        Assert.Equal("31%", m.Tiles.OverviewNote);
        Assert.Equal("412 GB free · v1.0.48", m.Nest.Text);
        Assert.Equal(SideDot.Ok, m.Nest.Dot);
        Assert.Equal(SideTone.Neutral, m.Nest.Tone);

        var laptop = m.Others.Single(o => o.Name == "LAPTOP");
        Assert.Equal("↓ 4.10 MB/s", laptop.Text);
        Assert.Equal(OtherKind.Live, laptop.Kind);
        Assert.Equal("LAPTOP, online, getting files at 4.10 MB/s", laptop.Tooltip);
        Assert.Equal("↓ 2.60 MB/s", m.Others.Single(o => o.Name == "MINI-PC").Text);
        Assert.Equal("DESKTOP, this computer, sending 4.90 MB/s", m.This.Tooltip);
    }

    [Fact]
    public void SyncingWithoutATransferHasNoBar()
    {
        var m = Build(Base(RunnerStatus.Syncing) with { Stage = SyncStage.Checking, CheckedFiles = 5, CheckTotal = 10 });
        Assert.Equal("This one · checking files", m.This.Text);
        Assert.Null(m.This.BarPercent);
        Assert.Null(m.RingPercent);
    }

    [Fact]
    public void SyncedSaysUpToDateAndTheOthersAreInSync()
    {
        var m = Build(Base());
        Assert.Equal("This one · up to date", m.This.Text);
        Assert.Equal(SideTone.Neutral, m.This.Tone);
        Assert.False(m.This.Highlighted);
        Assert.False(m.This.ShowBar);
        Assert.Equal("Done", m.Tiles.OverviewNote);
        Assert.Equal(["in sync", "in sync", "can’t reach", "can’t reach", "can’t reach"], m.Others.Select(o => o.Text));
        Assert.Equal(OtherKind.Ok, m.Others[0].Kind);
    }

    [Fact]
    public void WaitingNamesTheComputerAndTheOthersAreNext()
    {
        var s = Base() with
        {
            WaitingFor = new PeerWait("LAPTOP", 340, 212),
            Peers = Peers(0, laptopUp: 3.8 * MB),
        };
        var m = Build(s);
        Assert.Equal("This one · waits for LAPTOP", m.This.Text);
        Assert.Equal(SideDot.Ok, m.This.Dot);
        Assert.False(m.This.Highlighted);
        Assert.Equal("Wait", m.Tiles.OverviewNote);
        Assert.Equal("↑ 3.80 MB/s", m.Others[0].Text);
        Assert.Equal("LAPTOP", m.Others[0].Name);
        Assert.Equal("next", m.Others[1].Text);
        Assert.Equal(OtherKind.Next, m.Others[1].Kind);
        Assert.Equal("MINI-PC", m.Others[1].Name);
    }

    [Fact]
    public void StoppedIsAmberAndTheOthersJustSayOnline()
    {
        var m = Build(Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.MassDelete, PendingDeletes = 37 });
        Assert.Equal("This one · stopped to be safe", m.This.Text);
        Assert.Equal(SideDot.Warn, m.This.Dot);
        Assert.Equal(SideTone.Warn, m.This.Tone);
        Assert.Equal("Stop", m.Tiles.OverviewNote);
        Assert.Equal("online", m.Others[0].Text);
        Assert.Equal(OtherKind.Online, m.Others[0].Kind);
        Assert.Equal(SideTone.Neutral, m.Nest.Tone);
    }

    [Fact]
    public void SignInIsAmberAndTheNestWaitsForYou()
    {
        var m = Build(Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignInRequired });
        Assert.Equal("This one · needs to sign in", m.This.Text);
        Assert.Equal(SideDot.Warn, m.This.Dot);
        Assert.Equal("Sign in", m.Tiles.OverviewNote);
        Assert.Equal("Waiting for you to sign in", m.Nest.Text);
        Assert.Equal(SideDot.Warn, m.Nest.Dot);
        Assert.Equal(SideTone.Warn, m.Nest.Tone);
    }

    [Fact]
    public void SignedOutIsRed()
    {
        var m = Build(Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignedOut });
        Assert.Equal("This one · signed out", m.This.Text);
        Assert.Equal(SideDot.Bad, m.This.Dot);
        Assert.Equal(SideTone.Bad, m.This.Tone);
        Assert.Equal("Off", m.Tiles.OverviewNote);
        Assert.Equal("This computer was removed", m.Nest.Text);
        Assert.Equal(SideDot.Bad, m.Nest.Dot);
        Assert.Equal(SideTone.Bad, m.Nest.Tone);
    }

    [Fact]
    public void SignedOutHereIsGreyNotRed()
    {
        var m = Build(StatusSnapshot.SignedOut, activity: 0, attention: 0);
        Assert.Equal("This one · signed out", m.This.Text);
        Assert.Equal((SideDot.Off, SideTone.Neutral), (m.This.Dot, m.This.Tone));
        Assert.Equal("Signed out", m.Nest.Text);
        Assert.Equal((SideDot.Off, SideTone.Neutral), (m.Nest.Dot, m.Nest.Tone));
        Assert.Equal("signed out", m.GroupNote);
        Assert.Equal("Off", m.Tiles.OverviewNote);
    }

    [Fact]
    public void OfflineCannotCheckTheOthersAndShowsWhenTheyWereSeen()
    {
        var s = Base(RunnerStatus.Offline) with { Server = null };
        s = s with { Devices = [Device("DESKTOP", true), Device("LAPTOP", true, TimeSpan.FromMinutes(75)), Device("MINI-PC", false, TimeSpan.FromMinutes(80))] };
        var m = Build(s);
        Assert.Equal("This one · offline", m.This.Text);
        Assert.Equal(SideDot.Off, m.This.Dot);
        Assert.False(m.This.Highlighted);
        Assert.Equal("Off", m.Tiles.OverviewNote);
        Assert.Equal("Can’t reach it · trying again", m.Nest.Text);
        Assert.Equal(SideDot.Bad, m.Nest.Dot);
        Assert.Equal("can’t check now", m.GroupNote);
        Assert.Equal("offline", m.Tiles.DevicesNote);
        Assert.All(m.Others, o => Assert.Equal(OtherKind.LastSeen, o.Kind));
        Assert.Equal("seen " + Now.AddMinutes(-75).ToLocalTime().ToString("HH:mm"), m.Others[0].Text);
        Assert.False(m.Others[0].Reachable);
        Assert.Equal(SideDot.Off, m.Others[0].Dot);
    }

    [Fact]
    public void PausedIsGreyAndOthersStayOnline()
    {
        var m = Build(Base(RunnerStatus.Paused) with { Paused = true });
        Assert.Equal("This one · paused", m.This.Text);
        Assert.Equal(SideDot.Off, m.This.Dot);
        Assert.False(m.This.Highlighted);
        Assert.Equal("Paused", m.Tiles.OverviewNote);
        Assert.Equal("online", m.Others[0].Text);
    }

    [Theory]
    [InlineData(BlockReason.FolderMissing)]
    [InlineData(BlockReason.MarkerMissing)]
    [InlineData(BlockReason.MarkerMismatch)]
    public void MissingFolderIsAmber(BlockReason reason)
    {
        var m = Build(Base(RunnerStatus.Blocked) with { BlockReason = reason });
        Assert.Equal("This one · folder is missing", m.This.Text);
        Assert.Equal(SideDot.Warn, m.This.Dot);
        Assert.Equal(SideTone.Warn, m.This.Tone);
        Assert.Equal("Stop", m.Tiles.OverviewNote);
    }

    // ------------------------------------------------------------------ the group of other computers

    [Fact]
    public void TheGroupSaysHowManyAreOnlineAndPutsThemFirst()
    {
        var m = Build(Base());
        Assert.Equal("2 of 5 online", m.GroupNote);
        Assert.Equal(["LAPTOP", "MINI-PC", "MEDIA-PC", "OFFICE-PC", "KITCHEN-PC"], m.Others.Select(o => o.Name));
        Assert.Equal(0, m.MoreCount);
        Assert.False(m.ShowsMore);
        Assert.Equal("3 on", m.Tiles.DevicesNote);
        Assert.Equal(OtherKind.CantReach, m.Others[2].Kind);
        Assert.Equal(SideDot.Bad, m.Others[2].Dot);
        Assert.Equal("MEDIA-PC, can’t be reached", m.Others[2].Tooltip);
    }

    [Fact]
    public void MoreThanFiveComputersShowFiveAndAPlusCount()
    {
        var devices = new List<DeviceInfo> { Device("DESKTOP", true) };
        for (var i = 1; i <= 8; i++)
            devices.Add(Device($"PC-{i}", i <= 2, TimeSpan.FromHours(i)));
        var m = Build(Base() with { Devices = devices });
        Assert.Equal(5, m.Others.Count);
        Assert.Equal(8, m.AllOthers.Count);
        Assert.Equal(3, m.MoreCount);
        Assert.Equal("+3", m.MoreText);
        Assert.True(m.ShowsMore);
        Assert.Equal("2 of 8 online", m.GroupNote);
        Assert.Equal(["PC-1", "PC-2"], m.Others.Take(2).Select(o => o.Name)); // online first
        // The dock shows 2, then "+6" with the rest in its tooltip.
        Assert.Equal(2, m.DockOthers.Count);
        Assert.Equal(6, m.DockMoreCount);
        Assert.Equal("+6", m.DockMoreText);
        Assert.Equal("6 more computers", m.DockMoreLabel);
        Assert.Equal(6, m.DockMore.Count);
    }

    [Fact]
    public void ExactlyFiveComputersNeedNoPlus()
    {
        var m = Build(Base());
        Assert.Equal(5, m.AllOthers.Count);
        Assert.Equal(3, m.DockMoreCount);
        Assert.Equal("3 more computers", m.DockMoreLabel);
    }

    [Fact]
    public void RetiredComputersAreLeftOut()
    {
        var m = Build(Base() with { Devices = [Device("DESKTOP", true), Device("OLD-PC", false, TimeSpan.FromDays(90)), Device("LAPTOP", true)] });
        Assert.Equal(["LAPTOP"], m.Others.Select(o => o.Name));
    }

    [Fact]
    public void WithoutAListTheGroupIsEmpty()
    {
        var m = Build(Base() with { Devices = null });
        Assert.Empty(m.AllOthers);
        Assert.Equal(string.Empty, m.Tiles.DevicesNote);
        Assert.Equal("0 of 0 online", m.GroupNote);
    }

    [Fact]
    public void ALaptopLikeNameGetsALaptopIconForNow()
    {
        Assert.Equal(DeviceIconKind.Laptop, DeviceIcon.Guess("LAPTOP"));
        Assert.Equal(DeviceIconKind.Laptop, DeviceIcon.Guess("Marias-MacBook"));
        Assert.Equal(DeviceIconKind.Desktop, DeviceIcon.Guess("MINI-PC"));
        Assert.Equal(DeviceIconKind.Desktop, DeviceIcon.Guess("DESKTOP"));
    }

    // ------------------------------------------------------------------ tiles, account, update card

    [Fact]
    public void TilesCarryTheLiveNotes()
    {
        var m = Build(Base(), activity: 37, attention: 3);
        Assert.Equal("37", m.Tiles.ActivityNote);
        Assert.Equal("3", m.Tiles.AttentionNote);
        Assert.True(m.Tiles.AttentionAlert);
        var none = Build(Base(), attention: 0);
        Assert.False(none.Tiles.AttentionAlert);
        Assert.Equal(string.Empty, none.Tiles.AttentionNote);
    }

    [Fact]
    public void TheAccountButtonShowsTheInitial()
    {
        var m = Build(Base() with { AccountEmail = "alex@example.com" });
        Assert.Equal("A", m.AccountInitial);
        Assert.Equal("Account: alex@example.com", m.AccountLabel);
        var noAccount = Build(Base());
        Assert.Equal("D", noAccount.AccountInitial);
        Assert.Equal("Account", noAccount.AccountLabel);
    }

    [Fact]
    public void TheUpdateCardFollowsWhatTheControllerSays()
    {
        Assert.False(SideUpdate.From(null, "", "").Visible);
        var ready = SideUpdate.From("Pairnets 1.0.49 is available", "You have 1.0.48.", "Update now");
        Assert.Equal(UpdateCardKind.Available, ready.Kind);
        Assert.Equal("Pairnets 1.0.49 is ready", ready.Title);
        Assert.Equal("Update now", ready.Button);
        Assert.False(ready.ShowBar);

        var downloading = SideUpdate.From("Downloading Pairnets 1.0.49…", "Syncing continues.", "Update now", 64, busy: true);
        Assert.Equal(UpdateCardKind.Downloading, downloading.Kind);
        Assert.Equal("Getting Pairnets 1.0.49", downloading.Title);
        Assert.Equal(64, downloading.Percent);
        Assert.Equal("64%", downloading.PercentText);
        Assert.True(downloading.ShowBar);

        var failed = SideUpdate.From("Pairnets 1.0.49 could not be installed", "No space.", "Open download page");
        Assert.Equal(UpdateCardKind.Failed, failed.Kind);
        Assert.Equal("Open download page", failed.Button);

        Assert.Same(SideUpdate.None, Build(Base()).Update);
        Assert.Equal(ready, Build(Base(), update: ready).Update);
    }
}
