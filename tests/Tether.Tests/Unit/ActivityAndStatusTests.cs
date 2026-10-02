using Tether.Core.Client;
using Tether.Core.Paths;
using Tether.Core.Sync;

namespace Tether.Tests.Unit;

public class ActivityAndStatusTests
{
    [Fact]
    public void FeedIsBoundedAndNewestFirst()
    {
        var feed = new ActivityFeed(capacity: 3);
        var seen = 0;
        feed.Added += _ => seen++;
        for (var i = 0; i < 5; i++)
            feed.Add(ActivityKind.Uploaded, $"f{i}", $"Uploaded f{i}");
        Assert.Equal(["f4", "f3", "f2"], feed.Items.Select(i => i.Path));
        Assert.Equal(5, seen);
    }

    [Fact]
    public void SnapshotComputesPercentHeadlineAndFix()
    {
        var s = StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, CurrentPath = "big.bin", BytesDone = 50, BytesTotal = 200 };
        Assert.Equal(25, s.Percent);
        Assert.True(s.IsTransferring);
        Assert.Equal("Syncing…", s.Headline);
        Assert.Null((s with { BytesTotal = 0 }).Percent);
        Assert.Equal("Not synced yet", s.LastSyncText);

        var blocked = s with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.ServerRolledBack };
        Assert.Equal("Re-link to this server…", blocked.FixLabel);
        Assert.Equal("Locate the sync folder…", (blocked with { BlockReason = BlockReason.MarkerMissing }).FixLabel);
        Assert.Equal("Allow these deletions (7)…", (blocked with { BlockReason = BlockReason.FolderEmpty, PendingDeletes = 7 }).FixLabel);
    }

    [Fact]
    public void CaseKeyFoldsUnicodeNormalization() =>
        Assert.Equal(PathRules.CaseKey("Café/X.txt"), PathRules.CaseKey("café/x.txt"));
}
