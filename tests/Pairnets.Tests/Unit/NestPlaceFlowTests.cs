using System.Text.Json;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>What the "Where your nest keeps files" window shows in both apps, step by step.</summary>
public sealed class NestPlaceFlowTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    public static NestStorage Sample(StorageMoveStatus? status = null, string current = "/var/lib/pairnets")
    {
        var drives = JsonSerializer.Deserialize<StorageDrivesFile>(StorageMoverTests.SampleDrives, PairnetsJson.Options)!;
        return new NestStorage(true, current, "/var/lib/pairnets", current == "/var/lib/pairnets", false, 3_960_000_000_000, 4_400_000_000_000,
            drives.Drives!, drives.ListedAt, status);
    }

    [Fact]
    public void TheSettingsRowSaysWhereAndHowMuchIsFree()
    {
        Assert.Equal("/var/lib/pairnets · 3.60 TB free", NestPlaceFlow.PlaceLine(Sample()));
        Assert.Equal("/mnt/data/pairnets", NestPlaceFlow.PlaceLine(Sample(current: "/mnt/data/pairnets") with { Free = null }));
    }

    [Fact]
    public void TheListWaitsForTheDrivesThenMarksTheCurrentOneAndGreysTheUnusable()
    {
        var flow = new NestPlaceFlow(_clock);
        Assert.Equal(NestPlaceStep.Loading, flow.Step);
        flow.Seen(Sample() with { Drives = [] });
        Assert.Equal(NestPlaceStep.Loading, flow.Step);
        Assert.Contains("Looking", flow.Explanation, StringComparison.Ordinal);
        _clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Contains("has not listed", flow.Explanation, StringComparison.Ordinal);

        flow.Seen(Sample());
        Assert.Equal(NestPlaceStep.Choose, flow.Step);
        var rows = flow.Rows();
        Assert.Equal(["Main disk", "data", "STICK"], rows.Select(r => r.Label));
        Assert.True(rows[0].Current);
        Assert.Equal("3.64 TB free of 4.00 TB", rows[1].Space);
        Assert.False(rows[2].Usable);
        Assert.Equal("This drive has no Linux file permissions (vfat); format it as ext4 to use it.", rows[2].Reason);

        Assert.False(flow.Choose("/var/lib/pairnets")); // where the files are already
        Assert.False(flow.Choose("/media/stick/pairnets")); // greyed
        Assert.True(flow.Choose("/mnt/data/pairnets"));
        Assert.Equal(NestPlaceStep.Confirm, flow.Step);
        Assert.Equal("Move your nest's files to /mnt/data/pairnets? Your nest keeps running while they copy, then pauses for a moment to switch over. The old copy is kept until you delete it.",
            flow.Explanation);
        flow.Back();
        Assert.Equal(NestPlaceStep.Choose, flow.Step);
    }

    [Fact]
    public void AMoveIsFollowedThroughTheSwitchOverToItsResult()
    {
        var old = new StorageMoveStatus(StorageMoveStatus.Succeeded, 100, "Done long ago.", "/x", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var flow = new NestPlaceFlow(_clock);
        flow.Seen(Sample(old));
        Assert.Equal(NestPlaceStep.Choose, flow.Step); // an old result is not this window's
        flow.Choose("/mnt/data/pairnets");
        flow.Asking();
        Assert.Equal(NestPlaceStep.Moving, flow.Step);
        Assert.Equal("Starting the move…", flow.ProgressText);
        flow.Answered(new PairnetsApiClient.StorageMoveAnswer(PairnetsApiClient.StorageMoveOutcome.Requested, null,
            Sample(new StorageMoveStatus(StorageMoveStatus.Requested, 0, "Waiting for your nest to start the move."))));
        Assert.Equal("Starting the move…", flow.ProgressText);

        // The helper took the request but has not written yet: the old result still shows, and is not taken for this one.
        flow.Seen(Sample(old));
        Assert.Equal(NestPlaceStep.Moving, flow.Step);

        flow.Seen(Sample(new StorageMoveStatus(StorageMoveStatus.Running, 40, "Copying your files to the new drive")));
        Assert.Equal("Copying your files… 40%", flow.ProgressText);
        Assert.Equal(40, flow.Percent);
        flow.Seen(Sample(new StorageMoveStatus(StorageMoveStatus.Running, 96, "Switching your nest over to the new drive")));
        Assert.Equal("Switching over…", flow.ProgressText);

        // The nest restarts: not an error.
        flow.Unreachable();
        _clock.Advance(TimeSpan.FromMinutes(2));
        flow.Unreachable();
        Assert.Equal(NestPlaceStep.Moving, flow.Step);
        Assert.Equal("Switching over… your nest is restarting", flow.ProgressText);

        flow.Seen(Sample(new StorageMoveStatus(StorageMoveStatus.Succeeded, 100,
            "Done. Your nest now keeps its files in /mnt/data/pairnets. The old copy is kept in /var/lib/pairnets-before-move-x until you delete it.",
            "/var/lib/pairnets-before-move-x", _clock.GetUtcNow()), current: "/mnt/data/pairnets"));
        Assert.Equal(NestPlaceStep.Done, flow.Step);
        Assert.Equal("/var/lib/pairnets-before-move-x", flow.OldCopy);
        Assert.Equal("sudo rm -rf '/var/lib/pairnets-before-move-x'", flow.DeleteCommand);
        Assert.Contains("/mnt/data/pairnets", flow.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedMoveSaysWhyAndTryAgainGoesBackToTheList()
    {
        var flow = new NestPlaceFlow(_clock);
        flow.Seen(Sample());
        flow.Choose("/mnt/data/pairnets");
        flow.Asking();
        flow.Seen(Sample(new StorageMoveStatus(StorageMoveStatus.Running, 0, "Checking the drive")));
        Assert.Equal("Checking the drive…", flow.ProgressText);
        flow.Seen(Sample(new StorageMoveStatus(StorageMoveStatus.Failed, 0, "That drive can't be used: it is read-only. Nothing was changed.")));
        Assert.Equal(NestPlaceStep.Failed, flow.Step);
        Assert.Equal("That drive can't be used: it is read-only. Nothing was changed.", flow.Explanation);
        flow.Retry();
        Assert.Equal(NestPlaceStep.Choose, flow.Step);

        // A refusal from the nest itself (busy, set up by hand, asked too soon) ends the same way.
        flow.Choose("/mnt/data/pairnets");
        flow.Asking();
        flow.Answered(new PairnetsApiClient.StorageMoveAnswer(PairnetsApiClient.StorageMoveOutcome.Refused, "A move was asked for less than a minute ago.", null));
        Assert.Equal(NestPlaceStep.Failed, flow.Step);
        Assert.Equal("A move was asked for less than a minute ago.", flow.Explanation);
    }

    [Fact]
    public void AMoveAlreadyRunningWhenTheWindowOpensIsShownAndANestThatStaysAwayIsReported()
    {
        var flow = new NestPlaceFlow(_clock);
        flow.Seen(Sample(new StorageMoveStatus(StorageMoveStatus.Running, 12, "Copying your files to the new drive")));
        Assert.Equal(NestPlaceStep.Moving, flow.Step);
        flow.Unreachable();
        _clock.Advance(TimeSpan.FromMinutes(11));
        flow.Unreachable();
        Assert.Equal(NestPlaceStep.Failed, flow.Step);
        Assert.Contains("open this again later", flow.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestSetUpByHandOrWithoutTheHelperOnlyExplains()
    {
        var flow = new NestPlaceFlow(_clock);
        flow.Seen(Sample() with { ByHand = true });
        Assert.Equal(NestPlaceStep.Choose, flow.Step);
        Assert.Contains("/etc/fstab", flow.Explanation, StringComparison.Ordinal);

        var old = new NestPlaceFlow(_clock);
        old.Seen(new NestStorage(false, string.Empty, string.Empty, false, false, null, null, []));
        Assert.Equal(NestPlaceStep.Choose, old.Step);
        Assert.Contains("install command", old.Explanation, StringComparison.Ordinal);
        Assert.False(old.Choose("/mnt/data/pairnets"));
    }
}
