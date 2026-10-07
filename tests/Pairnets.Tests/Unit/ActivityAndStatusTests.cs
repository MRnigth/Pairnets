using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

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
    public void ConnectionIsShownOnlyOnceKnown()
    {
        var starting = StatusSnapshot.Initial;
        Assert.False(starting.IsConnected);
        Assert.Null(starting.ConnectionText); // still starting: no "Not connected" flash

        var synced = starting with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = DateTimeOffset.UtcNow };
        Assert.True(synced.IsConnected);
        Assert.Equal("Connected", synced.ConnectionText);

        var down = synced with { Status = RunnerStatus.Offline, Text = "Offline: timeout" };
        Assert.False(down.IsConnected);
        Assert.Equal("Not connected", down.ConnectionText);
    }

    [Fact]
    public void ServerVersionIsShownWithAWayToUpdate()
    {
        Assert.Null(StatusSnapshot.Initial.ServerVersionText); // before the server has answered
        Assert.False(StatusSnapshot.Initial.ServerIsOlder);

        var older = StatusSnapshot.Initial with { Server = new ServerInfo("id", 1, 1, "0.0.1") };
        Assert.Equal("Server 0.0.1", older.ServerVersionText);
        Assert.True(older.ServerIsOlder);
        Assert.Equal("Update server…", older.ServerUpdateButtonText);

        var current = StatusSnapshot.Initial with { Server = new ServerInfo("id", 1, 1, PairnetsInfo.ProductVersion) };
        Assert.Equal("Server " + PairnetsInfo.ProductVersion, current.ServerVersionText);
        Assert.False(current.ServerIsOlder);
        Assert.Equal("Check for update", current.ServerUpdateButtonText);

        var unknown = StatusSnapshot.Initial with { Server = new ServerInfo("id", 1, 1) }; // a server too old to say
        Assert.Equal("Server: old version", unknown.ServerVersionText);
        Assert.True(unknown.ServerIsOlder);
        Assert.Equal("Update server…", unknown.ServerUpdateButtonText);
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

public class StatusDetailTests
{
    [Theory]
    [InlineData(RunnerStatus.Syncing, "Syncing", "")]
    [InlineData(RunnerStatus.Idle, "Up to date", "")]
    [InlineData(RunnerStatus.Offline, "Offline: connection refused", "connection refused")]
    [InlineData(RunnerStatus.Error, "The server rejected the token", "The server rejected the token")]
    public void DetailDoesNotRepeatHeadline(RunnerStatus status, string text, string expected) =>
        Assert.Equal(expected, (StatusSnapshot.Initial with { Status = status, Text = text }).DetailText);
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(67108864, "64.0 MB")]
    [InlineData(104857600, "100 MB")]
    [InlineData(2254857830, "2.10 GB")]
    public void Bytes(long value, string expected) => Assert.Equal(expected, Format.Bytes(value));

    [Fact]
    public void RelativeTimes()
    {
        var now = new DateTimeOffset(2026, 10, 2, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", Format.RelativeTime(now.AddSeconds(-10), now));
        Assert.Equal("1 min ago", Format.RelativeTime(now.AddSeconds(-70), now));
        Assert.Equal("12 min ago", Format.RelativeTime(now.AddMinutes(-12), now));
        Assert.Contains("ago", Format.RelativeTime(now.AddHours(-2), now) + Format.RelativeTime(now.AddHours(-2), now));
    }

    [Fact]
    public void ActivityRowTexts()
    {
        var item = new ActivityItem(DateTimeOffset.Now, ActivityKind.Uploaded, "Projects/2026/plan.xlsx", "Uploaded: Projects/2026/plan.xlsx");
        Assert.Equal("plan.xlsx", item.Primary);
        Assert.Equal("Uploaded · Projects/2026", item.Secondary);
        Assert.Equal("just now", item.WhenText);
        var info = new ActivityItem(DateTimeOffset.Now, ActivityKind.Offline, null, "Cannot reach the server");
        Assert.Equal("Cannot reach the server", info.Primary);
        Assert.Equal("Offline", info.Secondary);
    }

    [Fact]
    public void TransferTexts()
    {
        var s = StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, CurrentPath = "Videos/a.mp4", Operation = "upload", BytesDone = 67108864, BytesTotal = 104857600, FilesDone = 2, FilesTotal = 5 };
        Assert.Equal("64.0 MB of 100 MB · 64%", s.ProgressText);
        Assert.Equal("File 3 of 5", s.FileCountText);
        Assert.Equal("a.mp4", s.CurrentFileName);
        Assert.Equal("Videos", s.CurrentFolder);
        Assert.Equal("Uploading", s.OperationText);
    }
}
