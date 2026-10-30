using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The Developer beta look: who gets it, and what its progress ring says.</summary>
public class DevBetaLookTests
{
    [Theory]
    [InlineData("stable", null, false)]
    [InlineData("public-beta", null, false)]
    [InlineData("developer-beta", null, true)]
    [InlineData("main", null, false)]
    [InlineData(null, null, false)]
    [InlineData("stable", "devbeta", true)] // the override, for local screenshots and tests
    [InlineData("stable", " DevBeta ", true)]
    [InlineData("developer-beta", "off", false)]
    [InlineData("public-beta", "nonsense", false)]
    public void OnlyDeveloperBetaGetsTheLookUnlessOverridden(string? channel, string? overrideValue, bool expected) =>
        Assert.Equal(expected, DevBetaLook.For(channel, overrideValue));

    private static StatusSnapshot Syncing() =>
        StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, Text = "Syncing", Devices = [] };

    [Fact]
    public void ReadingTheList_IsAnEmptyRingWithThinkingDots()
    {
        var r = ProgressRingModel.Build(Syncing(), NestMapState.Syncing);
        Assert.True(r.Thinking);
        Assert.True(r.Syncing);
        Assert.Equal(0, r.Reach);
        Assert.Equal(RingTone.Accent, r.Tone);
        Assert.Equal("Reading the server's list", r.Description);
    }

    [Fact]
    public void Moving_FilesDoneIsTheStrongLayerAndFilesMovingTheLighterOne()
    {
        var s = Syncing() with
        {
            CurrentPath = "a.jpg", FilesTotal = 1204, FilesDone = 312,
            Active = [new("a.jpg", "upload", 1, 10), new("b.jpg", "upload", 1, 10), new("c.jpg", "upload", 1, 10), new("d.jpg", "upload", 1, 10)],
        };
        var r = ProgressRingModel.Build(s, NestMapState.Syncing);
        Assert.Equal(312 / 1204.0, r.Done, 6);
        Assert.Equal(4 / 1204.0, r.Moving, 6);
        Assert.Equal(316 / 1204.0, r.Reach, 6);
        Assert.Equal("312", r.Big);
        Assert.Equal("of 1,204", r.Small);
        Assert.False(r.Thinking);
        Assert.Equal("312 of 1,204", r.Description);
    }

    [Fact]
    public void Checking_UsesTheFilesCheckedOfTheTotal()
    {
        var s = Syncing() with { Stage = SyncStage.Checking, Scan = new ScanProgress(300, 0, null, 0, 0, 1200) };
        var r = ProgressRingModel.Build(s, NestMapState.Syncing);
        Assert.Equal(0.25, r.Done, 6);
        Assert.Equal("of 1,200 checked", r.Small);
        Assert.False(r.Thinking);
    }

    [Fact]
    public void TheRingNeverGoesPastAFullTurn()
    {
        var s = Syncing() with
        {
            CurrentPath = "a", FilesTotal = 3, FilesDone = 9, // a count that ran ahead of its total
            Active = [new("a", "upload", 1, 10), new("b", "upload", 1, 10)],
        };
        var r = ProgressRingModel.Build(s, NestMapState.Syncing);
        Assert.Equal(1, r.Done);
        Assert.Equal(1, r.Reach);
        Assert.Equal("3", r.Big);
    }

    [Theory]
    [InlineData(NestMapState.UpToDate, RingTone.Ok, false)]
    [InlineData(NestMapState.Stopped, RingTone.Warn, false)]
    [InlineData(NestMapState.Waiting, RingTone.Warn, false)]
    [InlineData(NestMapState.Offline, RingTone.Bad, false)]
    [InlineData(NestMapState.SignedOut, RingTone.Bad, false)]
    [InlineData(NestMapState.Paused, RingTone.Quiet, false)]
    [InlineData(NestMapState.SignedOutByYou, RingTone.Quiet, false)]
    public void EachStateHasItsColourAndOnlySyncingMoves(NestMapState state, RingTone tone, bool syncing)
    {
        var r = ProgressRingModel.Build(StatusSnapshot.Initial with { Devices = [] }, state);
        Assert.Equal(tone, r.Tone);
        Assert.Equal(syncing, r.Syncing);
        Assert.False(r.Thinking);
    }

    [Fact]
    public void UpToDate_IsAFullGreenRing() =>
        Assert.Equal(1, ProgressRingModel.Build(StatusSnapshot.Initial with { Devices = [] }, NestMapState.UpToDate).Reach);
}
