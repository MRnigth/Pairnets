using Tether.Core.Client;
using Tether.Core.Sync;

namespace Tether.Tests.Unit;

/// <summary>When the windows celebrate (both apps): the ripple after a sync that moved files, and the first-sync confetti.</summary>
public class StatusMomentsTests
{
    private static readonly StatusSnapshot Idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = DateTimeOffset.UtcNow };
    private static readonly StatusSnapshot Checking = StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, Text = "Syncing" };
    private static readonly StatusSnapshot Uploading = Checking with { CurrentPath = "a.txt", Operation = "upload", FilesTotal = 2 };

    [Fact]
    public void ASyncThatMovedFilesIsMarkedOnceWhenItFinishes()
    {
        var moments = new StatusMoments();
        Assert.Equal(StatusMoment.None, moments.Next(Idle)); // the first look only sets the baseline
        Assert.Equal(StatusMoment.None, moments.Next(Uploading));
        Assert.Equal(StatusMoment.None, moments.Next(Uploading));
        Assert.Equal(StatusMoment.SyncFinished, moments.Next(Idle));
        Assert.Equal(StatusMoment.None, moments.Next(Idle)); // 4 refreshes a second: still once
    }

    [Fact]
    public void APassThatMovedNothingIsNotAMoment()
    {
        var moments = new StatusMoments();
        moments.Next(Idle);
        moments.Next(Checking);
        Assert.Equal(StatusMoment.None, moments.Next(Idle));
    }

    [Fact]
    public void NoCelebrationWhenTheSyncEndsBadlyOrWaits()
    {
        var moments = new StatusMoments();
        moments.Next(Uploading);
        Assert.Equal(StatusMoment.None, moments.Next(Idle with { Status = RunnerStatus.Error, Text = "Synced with 1 error(s), 0 warning(s)" }));
        Assert.Equal(StatusMoment.None, moments.Next(Idle)); // the error ended that pass

        moments.Next(Uploading);
        Assert.Equal(StatusMoment.None, moments.Next(Idle with { WaitingFor = new PeerWait("DESKTOP", 340, 12) }));
    }

    [Fact]
    public void ResetForgetsAPassSeenBeforeTheWindowWasHidden()
    {
        var moments = new StatusMoments();
        moments.Next(Uploading);
        moments.Reset();
        Assert.Equal(StatusMoment.None, moments.Next(Idle));
    }

    [Fact]
    public void TheFirstSyncAfterSetupIsCelebratedOnce()
    {
        var moments = new StatusMoments();
        Assert.Equal(StatusMoment.None, moments.Next(Idle with { LastSyncAt = null })); // starting, before any pass
        moments.ExpectFirstSync();
        Assert.Equal(StatusMoment.None, moments.Next(Idle with { LastSyncAt = null }));
        Assert.Equal(StatusMoment.None, moments.Next(Checking));
        Assert.Equal(StatusMoment.FirstSync, moments.Next(Idle));
        moments.Next(Uploading);
        Assert.Equal(StatusMoment.SyncFinished, moments.Next(Idle)); // later syncs only ripple
    }

    [Fact]
    public void HidingTheWindowCancelsTheFirstSyncConfetti()
    {
        var moments = new StatusMoments();
        moments.ExpectFirstSync();
        moments.Reset();
        Assert.Equal(StatusMoment.None, moments.Next(Idle));
    }

    [Fact]
    public void ConfettiFliesOnlyDuringTheBurstAndFades()
    {
        Assert.Empty(ConfettiBurst.At(0));
        Assert.Empty(ConfettiBurst.At(1));
        var middle = ConfettiBurst.At(0.5).ToList();
        Assert.Equal(36, middle.Count);
        Assert.All(middle, p => Assert.InRange(p.Color, 0, ConfettiBurst.ColorCount - 1));
        Assert.All(middle, p => Assert.Equal(1, p.Opacity));
        Assert.Contains(middle, p => p.X > 50); // spread out from the badge
        Assert.All(ConfettiBurst.At(0.95), p => Assert.True(p.Opacity < 0.2));
        Assert.Equal(middle, ConfettiBurst.At(0.5).ToList()); // the same every time
    }
}
