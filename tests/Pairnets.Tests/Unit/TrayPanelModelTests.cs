using System.Collections.ObjectModel;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The model behind the tray panel: the three designed states, a branch per online computer, one red row, the changes.</summary>
public class TrayPanelModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
    private const string Me = "DESKTOP";
    private const double MB = 1 << 20;

    private static DeviceInfo Dev(string name, bool online = true, double seenMinutesAgo = 0) =>
        new(name, Now.AddDays(-30), Now.AddMinutes(-seenMinutesAgo), online);

    private static StatusSnapshot Six(RunnerStatus status) => StatusSnapshot.Initial with
    {
        Status = status, Text = status.ToString(), LastSyncAt = Now.AddMinutes(-15), Server = Server,
        Devices = [Dev(Me), Dev("LAPTOP"), Dev("MINI-PC"), Dev("MEDIA-PC", false, 60 * 24 * 2), Dev("OFFICE-PC", false, 60 * 24 * 3), Dev("KITCHEN-PC", false, 60 * 24 * 4)],
    };

    private static StatusSnapshot WithPeer(StatusSnapshot s, string name, TransferReport report) =>
        s with { Peers = new ReadOnlyDictionary<string, PeerActivity>(new Dictionary<string, PeerActivity>(s.Peers) { [name] = new PeerActivity(report, Now.AddSeconds(-1), 0, null) }) };

    private static StatusSnapshot Syncing()
    {
        var s = Six(RunnerStatus.Syncing) with
        {
            Stage = SyncStage.Transferring, CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 12, BytesTotal = 100,
            FilesDone = 37, FilesTotal = 120, PassBytesDone = 412L << 20, PassBytesTotal = 1331L << 20,
            BytesPerSecond = 4.9 * MB, UpBytesPerSecond = 4.9 * MB, MovingUp = true,
            Active = [new ActiveTransfer("Videos/presentation-final.mp4", "upload", 12, 100)],
        };
        s = WithPeer(s, "LAPTOP", new TransferReport(0, 4.1 * MB, 30, 120, 0, 0, Downloading: true));
        return WithPeer(s, "MINI-PC", new TransferReport(0, 2.6 * MB, 12, 120, 0, 0, Downloading: true));
    }

    private static TrayPanelModel Build(StatusSnapshot s) => TrayPanelModel.Build(s, Me, Now);

    [Fact]
    public void Syncing_ShowsProgressAndABranchWithASpeedPerOnlineComputer()
    {
        var m = Build(Syncing());

        Assert.Equal((NestMapState.Syncing, TrayTone.Syncing), (m.State, m.Tone));
        Assert.Equal("Syncing…", m.Headline);
        Assert.Equal("Uploading 120 files to your nest", m.Detail);
        Assert.Equal(30, m.Percent);
        Assert.True(m.ShowsProgress);
        Assert.StartsWith("37 of 120 files", m.FilesText);
        Assert.Equal("4 min left", m.LeftText);
        Assert.Equal("412 GB free", m.NestSub);

        Assert.True(m.This.IsThis);
        Assert.Equal("↑ 4.90 MB/s", m.This.Pill);
        Assert.Equal("this computer", m.This.Side);
        Assert.Equal(["LAPTOP", "MINI-PC"], m.Others.Select(o => o.Name));
        Assert.Equal(["↓ 4.10 MB/s", "↓ 2.60 MB/s"], m.Others.Select(o => o.Pill));
        Assert.All(m.Others, o => Assert.Equal(("getting them", TrayInk.Accent), (o.Side, o.SideInk)));
        Assert.Equal(3, m.Branches.Count());
    }

    [Fact]
    public void TheComputersThatCantBeReachedAreOneRedRow()
    {
        var m = Build(Syncing());
        var red = m.Unreachable!;

        Assert.Equal(3, red.Count);
        Assert.Equal("3 can’t be reached", red.Text);
        Assert.Equal("MEDIA-PC, OFFICE-PC and KITCHEN-PC can’t be reached", red.Tooltip);
        Assert.Equal(3, red.Icons.Count);
        Assert.DoesNotContain(m.Branches, b => b.HasCross);
        Assert.DoesNotContain(m.Branches, b => b.Name is "MEDIA-PC" or "OFFICE-PC" or "KITCHEN-PC");
    }

    [Fact]
    public void UpToDate_IsGreenWithEveryOnlineComputerInSync()
    {
        var m = Build(Six(RunnerStatus.Idle));

        Assert.Equal((NestMapState.UpToDate, TrayTone.Ok), (m.State, m.Tone));
        Assert.Equal("Everything is up to date", m.Headline);
        Assert.Equal("Last synced today at " + Now.AddMinutes(-15).ToLocalTime().ToString("HH:mm"), m.Detail);
        Assert.False(m.ShowsProgress);
        Assert.Equal(("in sync", NestLineKind.Ok), (m.This.Word, m.This.Kind));
        Assert.True(m.This.HasWord);
        Assert.All(m.Others, o => Assert.Equal(("in sync", TrayInk.Ok, NestLineKind.Ok), (o.Side, o.SideInk, o.Kind)));
        Assert.Equal("Pause", m.PauseLabel);
        Assert.False(m.ResumeIsPrimary);
    }

    [Fact]
    public void Paused_SaysSoAndResumeBecomesTheMainButton()
    {
        var m = Build(Six(RunnerStatus.Paused) with { Paused = true });

        Assert.Equal((NestMapState.Paused, TrayTone.Paused), (m.State, m.Tone));
        Assert.Equal("Syncing is paused", m.Headline);
        Assert.Equal("New changes wait here until you resume.", m.PausedNote);
        Assert.Equal(("paused", NestLineKind.Next, NestDot.Off), (m.This.Word, m.This.Kind, m.This.Dot));
        Assert.True(m.This.Dashed);
        Assert.All(m.Others, o => Assert.Equal(("online", NestLineKind.Idle), (o.Side, o.Kind)));
        Assert.Equal("Resume", m.PauseLabel);
        Assert.True(m.ResumeIsPrimary);
    }

    [Fact]
    public void WhenThisComputerCantReachTheNest_TheOthersAreGreyBranchesNotRed()
    {
        var m = Build(Six(RunnerStatus.Offline) with { Text = "Offline: timeout" });

        Assert.Equal(TrayTone.Bad, m.Tone);
        Assert.True(m.This.HasCross);
        Assert.Equal("can’t reach", m.This.Word);
        Assert.Null(m.Unreachable);
        Assert.All(m.Others, o => Assert.StartsWith("seen ", o.Side));
        Assert.True(m.Others.All(o => o.Dashed));
    }

    [Fact]
    public void ABlockedSyncKeepsItsFixButtonAndAnAmberCircle()
    {
        var m = Build(Six(RunnerStatus.Blocked) with { BlockReason = BlockReason.MassDelete, PendingDeletes = 37 });

        Assert.Equal(TrayTone.Warn, m.Tone);
        Assert.StartsWith("Allow these deletions", m.FixLabel);
        Assert.Equal(2, m.ChangesLimit(attention: true)); // never fewer than two rows
    }

    [Fact]
    public void AtMostFiveOthersAndTheRestAreCounted()
    {
        var devices = Enumerable.Range(1, 8).Select(i => Dev("PC-" + i)).Prepend(Dev(Me)).ToList();
        var m = Build(Six(RunnerStatus.Idle) with { Devices = devices });

        Assert.Equal(5, m.Others.Count);
        Assert.Equal("+3 more", m.MoreText);
    }

    [Fact]
    public void AttentionText_IsPluralAware()
    {
        Assert.Null(TrayPanelModel.AttentionText(0));
        Assert.Equal("1 thing needs you", TrayPanelModel.AttentionText(1));
        Assert.Equal("3 things need you", TrayPanelModel.AttentionText(3));
    }

    [Fact]
    public void LatestChanges_PutTheFileMovingNowFirst()
    {
        var s = Syncing();
        var items = new[]
        {
            new ActivityItem(Now.AddMinutes(-8), ActivityKind.Uploaded, "Photos/holiday-0412.jpg", "Uploaded"),
            new ActivityItem(Now.AddMinutes(-20), ActivityKind.Downloaded, "Notes/ideas.md", "Downloaded"),
            new ActivityItem(Now.AddMinutes(-30), ActivityKind.Conflict, "report (conflict).docx", "Conflict"),
        };
        var rows = TrayPanelModel.Changes(s, items, 3, Now);

        Assert.Equal(3, rows.Count);
        Assert.Equal(("presentation-final.mp4", "Uploading now", "12%", TrayChangeKind.Live), (rows[0].Name, rows[0].Sub, rows[0].Right, rows[0].Kind));
        Assert.Equal(("holiday-0412.jpg", "Uploaded", TrayChangeKind.Up), (rows[1].Name, rows[1].Sub, rows[1].Kind));
        Assert.Equal(TrayChangeKind.Down, rows[2].Kind);

        var idle = TrayPanelModel.Changes(Six(RunnerStatus.Idle), items, 2, Now);
        Assert.Equal(2, idle.Count);
        Assert.DoesNotContain(idle, r => r.Live);
    }

    [Fact]
    public void SyncedText_SaysTodayYesterdayOrTheDate()
    {
        var clock = Now.ToLocalTime().ToString("HH:mm");
        Assert.Equal("Last synced today at " + clock, TrayPanelModel.SyncedText(Now, Now));
        Assert.Equal("Last synced yesterday at " + clock, TrayPanelModel.SyncedText(Now.AddDays(-1), Now));
        Assert.Contains("Oct at", TrayPanelModel.SyncedText(Now.AddDays(-5), Now));
        Assert.Equal("Not synced yet", TrayPanelModel.SyncedText(null, Now));
    }
}
