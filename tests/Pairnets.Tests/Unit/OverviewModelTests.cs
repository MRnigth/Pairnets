using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The words and parts of the Overview page in each of its nine states.</summary>
public class OverviewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.48", 412L << 30, 1L << 40);

    private static StatusSnapshot Base(RunnerStatus status) =>
        StatusSnapshot.Initial with { Status = status, Text = status.ToString(), LastSyncAt = Now.AddMinutes(-15), Server = Server, Devices = [] };

    private static OverviewModel Build(StatusSnapshot s, int attention = 0, IReadOnlyList<ActivityItem>? recent = null) =>
        OverviewModel.Build(s, "DESKTOP", @"D:\Sync\Work", attention, recent ?? [], Now);

    [Fact]
    public void Syncing_ShowsTheBarWithTitlePercentAndSpeed()
    {
        var s = Base(RunnerStatus.Syncing) with
        {
            CurrentPath = "Photos/a.jpg", Operation = "upload", FilesTotal = 120, FilesDone = 37, PassBytesTotal = 1_300_000_000, PassBytesDone = 412_000_000,
            BytesPerSecond = 5_000_000, MovingUp = true,
            Active = [new ActiveTransfer("Photos/a.jpg", "upload", 10, 100)],
        };
        var m = Build(s);

        Assert.Equal(NestMapState.Syncing, m.State);
        Assert.True(m.IsSyncingBar);
        Assert.Equal("Syncing", m.Chip);
        Assert.Equal("Uploading 120 files to your nest", m.Title);
        Assert.Equal(31, m.Progress!.Percent);
        Assert.StartsWith("37 of 120 files", m.Progress.Left);
        Assert.Contains("left", m.Progress.Right);
        Assert.Equal(OverviewFlow.Up, m.Flow);
        Assert.True(m.ShowsMoving);
    }

    [Fact]
    public void Syncing_ADownloadAndASingleFileAreNamedPlainly()
    {
        var one = Base(RunnerStatus.Syncing) with { CurrentPath = "a/big.bin", Operation = "download", FilesTotal = 1, MovingDown = true, Active = [new ActiveTransfer("a/big.bin", "download", 1, 10)] };
        Assert.Equal("Downloading big.bin from your nest", Build(one).Title);
        Assert.Equal(OverviewFlow.Down, Build(one).Flow);
        Assert.Equal("Syncing…", Build(Base(RunnerStatus.Syncing)).Title);
    }

    [Fact]
    public void Syncing_WhileTheFolderIsReadTheFlowIsChecking()
    {
        var m = Build(Base(RunnerStatus.Syncing) with { Stage = SyncStage.Checking, CheckTotal = 200, CheckedFiles = 50 });
        Assert.Equal(OverviewFlow.Checking, m.Flow);
        Assert.Equal(25, m.Progress!.Percent);
    }

    [Fact]
    public void MovingNow_ListsFolderRowsFromTheBatch()
    {
        var s = Base(RunnerStatus.Syncing) with
        {
            CurrentPath = "Photos/Holiday/a.jpg", Operation = "upload", FilesTotal = 120, FilesDone = 37,
            Active = [new ActiveTransfer("Photos/Holiday/a.jpg", "upload", 1, 10)],
            Batch =
            [
                new BatchFile("Photos/Holiday/a.jpg", "upload", BatchFileState.Moving, 78),
                new BatchFile("Photos/Holiday/b.jpg", "upload", BatchFileState.Moving, 41),
                new BatchFile("Photos/Holiday/c.jpg", "upload", BatchFileState.Waiting),
                new BatchFile("movie.mp4", "upload", BatchFileState.Moving, 12),
            ],
        };
        var m = Build(s);

        Assert.Equal(2, m.Moving.Count);
        var folder = m.Moving[0];
        Assert.True(folder.IsFolder);
        Assert.Equal("Holiday", folder.Title);
        Assert.Equal("Photos · 3 files · 2 moving", folder.Sub);
        Assert.Equal(["a.jpg", "b.jpg"], folder.Files.Select(f => f.Name)); // what moves now; the waiting ones show when nothing moves
        Assert.Equal(78, folder.Files[0].Percent);
        Assert.Equal(OverviewIcon.Image, folder.Files[0].Icon);
        Assert.False(m.Moving[1].IsFolder);
        Assert.Equal("movie.mp4", m.Moving[1].Title);
        Assert.Equal(OverviewIcon.Video, m.Moving[1].Icon);
        Assert.Equal("3 of 120 files", m.MovingCount);
    }

    [Fact]
    public void UpToDate_SaysSoAndShowsLatestChangesAndFacts()
    {
        var recent = new[] { new ActivityItem(Now.AddMinutes(-5), ActivityKind.Uploaded, "report-q3.xlsx", "Uploaded: report-q3.xlsx") };
        var m = Build(Base(RunnerStatus.Idle), attention: 3, recent);

        Assert.Equal(NestMapState.UpToDate, m.State);
        Assert.Equal("Everything is up to date", m.Title);
        Assert.StartsWith("Last synced ", m.Detail);
        Assert.Contains("at ", m.Detail);
        Assert.Equal(OverviewIcon.Check, m.Glyph);
        Assert.Equal(OverviewLower.ChangesAndFacts, m.Lower);
        Assert.Equal("report-q3.xlsx", Assert.Single(m.Changes).Name);
        Assert.Equal(["Your folder", "Nest version", "Free space"], m.Facts.Select(f => f.Label));
        Assert.Equal("412 GB on the nest", m.Facts[2].Value);
        Assert.Equal("3 things need you", m.NeedsTitle);
    }

    [Fact]
    public void NeedsYou_NamesWhatIsWaiting()
    {
        var s = Base(RunnerStatus.Idle) with { JoinRequests = [new JoinRequest("c", "LAPTOP", "Windows", Now)] };
        Assert.Equal("A computer asks to join, and 2 items need a look.", Build(s, attention: 3).NeedsDetail);
        Assert.Equal("1 thing needs you", Build(s, attention: 1).NeedsTitle);
        Assert.Null(Build(s, attention: 0).NeedsTitle);
    }

    [Fact]
    public void Waiting_NamesTheComputerAndShowsItsBatch()
    {
        var s = Base(RunnerStatus.Idle) with { WaitingFor = new PeerWait("LAPTOP", 86, 46) };
        var m = Build(s);

        Assert.Equal(NestMapState.Waiting, m.State);
        Assert.Equal("Waiting for LAPTOP", m.Title);
        Assert.Contains("LAPTOP is sending a big batch", m.Detail);
        Assert.True(m.Pulses);
        Assert.Equal("LAPTOP is sending 86 files", m.WaitBox!.Title);
        Assert.Equal("46 of 86 files", m.WaitBox.Sub);
        Assert.Equal(53, m.WaitBox.Percent);
        Assert.Equal(OverviewAction.DownloadNow, m.WaitBox.Skip!.Action);
    }

    [Fact]
    public void Stopped_ListsWhatWouldBeDeletedAndOffersTheFix()
    {
        var s = Base(RunnerStatus.Blocked) with
        {
            BlockReason = BlockReason.MassDelete, PendingDeletes = 37,
            PendingDeletePaths = ["Drafts/old-draft.docx", "Finance/budget.xlsx", "Notes/meeting.md", "Photos/holiday.jpg", "x/y.txt"],
        };
        var m = Build(s, attention: 2);

        Assert.Equal(NestMapState.Stopped, m.State);
        Assert.Equal("Stopped to keep your files safe", m.Title);
        Assert.Equal("Needs your OK", m.Badge);
        Assert.Equal(OverviewTone.Warn, m.Border);
        Assert.Equal("37 files", m.DeleteBox!.CountText);
        Assert.Equal(["old-draft.docx", "budget.xlsx", "meeting.md", "holiday.jpg"], m.DeleteBox.Files.Select(f => f.Name));
        Assert.Equal("Drafts", m.DeleteBox.Files[0].Sub);
        Assert.Equal("and 33 more", m.DeleteBox.More);
        Assert.Equal("Allow these deletions (37)…", m.Buttons[0].Label);
        Assert.Equal(OverviewAction.Fix, m.Buttons[0].Action);
        Assert.Equal(OverviewAction.SeeList, m.Buttons[1].Action);
        Assert.True(m.HasFix);
        Assert.Equal(OverviewLower.Strip, m.Lower);
    }

    [Fact]
    public void Stopped_OtherReasonsKeepTheirOwnWords()
    {
        var s = Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.ServerChanged, Text = "The server is not the one this folder was synced with." };
        var m = Build(s);
        Assert.Equal(NestMapState.Stopped, m.State);
        Assert.Equal("Needs your decision", m.Title);
        Assert.Equal(s.Text, m.Detail);
        Assert.Null(m.DeleteBox);
        Assert.Equal("Re-link to this server…", Assert.Single(m.Buttons).Label);
    }

    [Fact]
    public void SignIn_ExplainsAndOffersTheBrowser()
    {
        var s = Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignInRequired, NestUrl = "https://nest.example.com" };
        var m = Build(s);

        Assert.Equal(NestMapState.SignIn, m.State);
        Assert.Equal("Sign in to your nest", m.Title);
        Assert.Equal(3, m.Checks.Count);
        Assert.Equal("Sign in with your browser", m.Buttons[0].Label);
        Assert.True(m.Buttons[0].Large);
        Assert.Equal("Opens nest.example.com in your browser", m.ButtonNote);
        Assert.Equal(["Your folder", "Account"], m.Facts.Select(f => f.Label));
    }

    [Fact]
    public void SignedOut_IsRedWithTwoWaysOut()
    {
        var m = Build(Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignedOut, NestUrl = "https://nest.example.com" });

        Assert.Equal(NestMapState.SignedOut, m.State);
        Assert.Equal(OverviewTone.Bad, m.Border);
        Assert.Equal("Signed out of your nest", m.Title);
        Assert.Equal("Not syncing", m.Badge);
        Assert.Equal(["Sign in again", "Open your account page"], m.Buttons.Select(b => b.Label));
        Assert.Equal(OverviewAction.OpenAccount, m.Buttons[1].Action);
        Assert.Equal("nest.example.com", m.Facts[1].Value);
    }

    [Fact]
    public void SignedOutHere_IsCalmWithOneWayBackIn()
    {
        var m = Build(StatusSnapshot.SignedOut);

        Assert.Equal(NestMapState.SignedOutByYou, m.State);
        Assert.NotEqual(OverviewTone.Bad, m.Border);
        Assert.Equal(OverviewTone.Neutral, m.GlyphTone);
        Assert.Equal("You’re signed out", m.Title);
        Assert.DoesNotContain("reach", m.Detail, StringComparison.OrdinalIgnoreCase);
        var button = Assert.Single(m.Buttons);
        Assert.Equal(("Sign in…", OverviewAction.Fix), (button.Label, button.Action));
        Assert.Null(m.HeadButton);
    }

    [Fact]
    public void Offline_ShowsTryNowAndWhatWaits()
    {
        var m = Build(Base(RunnerStatus.Offline) with { PendingChanges = 12, NestUrl = "https://nest.example.com" });

        Assert.Equal(NestMapState.Offline, m.State);
        Assert.Equal("Can’t reach your nest", m.Title);
        Assert.Equal(OverviewAction.SyncNow, m.HeadButton!.Action);
        Assert.Equal(3, m.Tiles.Count);
        Assert.Equal("12 changes", m.Tiles[1].Value);
        Assert.Equal("Waiting here", m.Tiles[1].Label);
    }

    [Fact]
    public void Paused_OffersResumeAsTheMainButton()
    {
        var m = Build(Base(RunnerStatus.Paused) with { Paused = true, PendingChanges = 5 });

        Assert.Equal(NestMapState.Paused, m.State);
        Assert.Equal("Paused", m.Title);
        Assert.Equal("Resume", m.HeadButton!.Label);
        Assert.True(m.HeadButton.Large);
        Assert.Equal(OverviewAction.TogglePause, m.HeadButton.Action);
        Assert.Contains(m.Infos, i => i.Text == "5 changes waiting here");
        Assert.Contains(m.Infos, i => i.Text == "Your other computers keep syncing");
    }

    [Fact]
    public void FolderMissing_OffersLocateAndLookAgain()
    {
        var m = Build(Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.FolderMissing });

        Assert.Equal(NestMapState.FolderMissing, m.State);
        Assert.Equal("Can’t find your sync folder", m.Title);
        Assert.Equal("Stopped", m.Badge);
        Assert.Equal(@"D:\Sync\Work", m.FolderBox!.Path);
        Assert.StartsWith("Not found · last seen ", m.FolderBox.Sub);
        Assert.Equal(["Locate the sync folder…", "Look again"], m.Buttons.Select(b => b.Label));
        Assert.Equal("Locate…", m.Facts[0].LinkText);
    }

    [Theory]
    [InlineData("photo.JPG", OverviewIcon.Image)]
    [InlineData("clip.mp4", OverviewIcon.Video)]
    [InlineData("notes.md", OverviewIcon.Document)]
    [InlineData("archive.zip", OverviewIcon.File)]
    public void FilesGetAPictureFromTheirName(string name, OverviewIcon icon) => Assert.Equal(icon, OverviewModel.IconFor(name));

    [Fact]
    public void ThePictureKnowsWhichWayFilesGo()
    {
        var other = new DeviceInfo("LAPTOP", Now.AddDays(-30), Now, true);
        var me = new DeviceInfo("DESKTOP", Now.AddDays(-30), Now, true);
        var waiting = Base(RunnerStatus.Idle) with
        {
            Devices = [me, other], WaitingFor = new PeerWait("LAPTOP", 86, 46),
            Peers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, PeerActivity>(new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(3_800_000, 0, 46, 86, 0, 0, Uploading: true), Now.AddSeconds(-1), 0, null),
            }),
        };
        var sending = NestMapModel.Build(waiting, "DESKTOP", Now).Others[0];
        Assert.True(sending.GoesUp);
        Assert.True(sending.HasPill);

        var getting = waiting with
        {
            Peers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, PeerActivity>(new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(0, 4_100_000, 30, 120, 0, 0, Downloading: true), Now.AddSeconds(-1), 0, null),
            }),
            WaitingFor = null,
        };
        Assert.False(NestMapModel.Build(getting, "DESKTOP", Now).Others[0].GoesUp);
    }
}
