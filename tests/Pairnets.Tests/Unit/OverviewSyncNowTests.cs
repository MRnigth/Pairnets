using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>
/// The header's "Sync now" follows the state (A1): the blue main button only when syncing is the useful thing, resting while
/// a sync runs, and plain wherever the card has its own action, so that one is the only blue button.
/// </summary>
public class OverviewSyncNowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.48", 412L << 30, 1L << 40);

    private static StatusSnapshot Base(RunnerStatus status) =>
        StatusSnapshot.Initial with { Status = status, Text = status.ToString(), LastSyncAt = Now.AddMinutes(-15), Server = Server, Devices = [] };

    private static OverviewModel Build(StatusSnapshot s) => OverviewModel.Build(s, "DESKTOP", @"D:\Sync\Work", 0, [], Now);

    [Theory]
    [InlineData(NestMapState.UpToDate, SyncNowLook.Primary)]
    [InlineData(NestMapState.Waiting, SyncNowLook.Primary)]
    [InlineData(NestMapState.Syncing, SyncNowLook.Busy)]
    [InlineData(NestMapState.Stopped, SyncNowLook.Plain)]
    [InlineData(NestMapState.SignIn, SyncNowLook.Plain)]
    [InlineData(NestMapState.SignedOut, SyncNowLook.Plain)]
    [InlineData(NestMapState.SignedOutByYou, SyncNowLook.Plain)]
    [InlineData(NestMapState.Offline, SyncNowLook.Plain)]
    [InlineData(NestMapState.Paused, SyncNowLook.Plain)]
    [InlineData(NestMapState.FolderMissing, SyncNowLook.Plain)]
    public void EachStateChoosesHowSyncNowLooks(NestMapState state, SyncNowLook look)
    {
        var syncNow = OverviewSyncNow.For(state);

        Assert.Equal(look, syncNow.Look);
        Assert.Equal(look == SyncNowLook.Primary, syncNow.IsPrimary);
        Assert.Equal(look != SyncNowLook.Busy, syncNow.IsEnabled);
        Assert.Equal("Sync now", syncNow.Label);
    }

    [Fact]
    public void TheModelCarriesTheLookOfItsState()
    {
        Assert.True(Build(Base(RunnerStatus.Idle)).SyncNow.IsPrimary);
        var syncing = Build(Base(RunnerStatus.Syncing) with { FilesTotal = 3, Operation = "upload", CurrentPath = "a.txt", MovingUp = true });
        Assert.Equal(SyncNowLook.Busy, syncing.SyncNow.Look);
        Assert.Equal("Pairnets is syncing now", syncing.SyncNow.ToolTip(mac: false));
        var signedOut = Build(Base(RunnerStatus.Blocked) with { BlockReason = BlockReason.SignedOut });
        Assert.Equal(SyncNowLook.Plain, signedOut.SyncNow.Look);
    }

    [Theory]
    [InlineData(RunnerStatus.Offline, BlockReason.None)]
    [InlineData(RunnerStatus.Blocked, BlockReason.SignedOut)]
    [InlineData(RunnerStatus.Blocked, BlockReason.MassDelete)]
    public void WhenTheCardHasItsOwnActionSyncNowIsNotBlue(RunnerStatus status, BlockReason reason)
    {
        var s = Base(status) with { BlockReason = reason, PendingDeletes = reason == BlockReason.MassDelete ? 5 : 0, PendingDeletePaths = ["a.txt"] };
        var m = Build(s);

        Assert.False(m.SyncNow.IsPrimary);
        var blue = m.Buttons.Count(b => b.Kind == OverviewButtonKind.Primary) + (m.HeadButton?.Kind == OverviewButtonKind.Primary ? 1 : 0);
        Assert.Equal(1, blue); // the card's own action is the one blue button
    }

    [Fact]
    public void TheTooltipNamesTheShortcut()
    {
        var idle = OverviewSyncNow.For(NestMapState.UpToDate);
        Assert.Equal("Sync now (Ctrl+R)", idle.ToolTip(mac: false));
        Assert.Equal("Sync now (⌘R)", idle.ToolTip(mac: true));
    }

    [Fact]
    public void TheDeleteConfirmationSaysWhatItDoes()
    {
        Assert.Equal(("Delete 37 files", "Cancel"), OverviewModel.DeleteButtons(37));
        Assert.Equal(("Delete 1 file", "Cancel"), OverviewModel.DeleteButtons(1));
        Assert.Equal(("Delete 1,200 files", "Cancel"), OverviewModel.DeleteButtons(1200));
    }
}
