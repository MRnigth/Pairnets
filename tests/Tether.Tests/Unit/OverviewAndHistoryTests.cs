using System.Collections.ObjectModel;
using Tether.Core;
using Tether.Core.Client;
using Tether.Core.Sync;

namespace Tether.Tests.Unit;

/// <summary>The shared logic behind the overview picture, the History page and the activity day headings.</summary>
public class OverviewAndHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);

    private static StatusSnapshot Connected(params DeviceInfo[] devices) =>
        StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = Now, Server = Server, Devices = devices };

    [Fact]
    public void MapShowsTheOtherComputerOnlineOrLastSeen()
    {
        var map = DeviceMap.Build(Connected(new DeviceInfo("MAC", true, Now), new DeviceInfo("DESKTOP", true, Now, "Windows 1.0.58")), "MAC", Now);
        Assert.Equal("MAC", map.Here.Name);
        Assert.Equal(NodeState.Online, map.Server.State);
        Assert.Equal("412 GB free", map.Server.Detail);
        Assert.Equal("DESKTOP", map.Other.Name);
        Assert.Equal("Online", map.Other.Detail);
        Assert.Equal("Windows 1.0.58", map.Other.Tip);
        Assert.True(map.HereLinked);
        Assert.True(map.OtherLinked);
        Assert.Equal(LinkFlow.None, map.HereFlow);
        Assert.Equal(0, map.MoreComputers);

        var away = DeviceMap.Build(Connected(new DeviceInfo("DESKTOP", false, Now.AddMinutes(-12))), "MAC", Now);
        Assert.Equal(NodeState.Offline, away.Other.State);
        Assert.Equal("Last seen 12 min ago", away.Other.Detail);
        Assert.False(away.OtherLinked);
    }

    [Fact]
    public void MapExplainsAMissingOrUnknownOtherComputer()
    {
        var alone = DeviceMap.Build(Connected(new DeviceInfo("MAC", true, Now)), "MAC", Now);
        Assert.Equal("Your other computer", alone.Other.Name);
        Assert.Equal(NodeState.Unknown, alone.Other.State);

        var oldServer = DeviceMap.Build(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Server = Server, DevicesUnsupported = true }, "MAC", Now);
        Assert.Equal("Update the server to see it", oldServer.Other.Detail);

        var offline = DeviceMap.Build(StatusSnapshot.Initial with { Status = RunnerStatus.Offline, Text = "Offline: timeout" }, "MAC", Now);
        Assert.Equal(NodeState.Offline, offline.Server.State);
        Assert.False(offline.HereLinked);
        Assert.False(offline.OtherLinked);

        // Long-gone names (a renamed computer) are left out; several recent ones are counted.
        var many = DeviceMap.Build(Connected(new DeviceInfo("OLD", false, Now.AddDays(-90)), new DeviceInfo("A", false, Now.AddHours(-1)),
            new DeviceInfo("B", true, Now), new DeviceInfo("C", false, Now.AddDays(-2))), "MAC", Now);
        Assert.Equal("B", many.Other.Name);
        Assert.Equal(2, many.MoreComputers);
        Assert.Equal("+2 more computers", many.MoreText);
    }

    [Fact]
    public void MapFlowsFollowTransfersAndTheOtherComputer()
    {
        var uploading = Connected(new DeviceInfo("DESKTOP", true, Now)) with
        {
            Status = RunnerStatus.Syncing,
            CurrentPath = "a",
            Operation = "upload",
            Active = [new ActiveTransfer("a", "upload", 1, 2)],
        };
        var map = DeviceMap.Build(uploading, "MAC", Now);
        Assert.Equal(LinkFlow.Up, map.HereFlow);
        Assert.Equal(LinkFlow.Down, map.OtherFlow); // what this computer uploads goes on to the other one

        var mixed = uploading with { Active = [new ActiveTransfer("a", "upload", 1, 2), new ActiveTransfer("b", "download", 1, 2)] };
        Assert.Equal(LinkFlow.Both, DeviceMap.Build(mixed, "MAC", Now).HereFlow);

        var heard = Connected(new DeviceInfo("DESKTOP", false, Now.AddMinutes(-3))) with
        {
            HeardFrom = new Dictionary<string, DateTimeOffset> { ["DESKTOP"] = Now.AddSeconds(-2) },
        };
        var sending = DeviceMap.Build(heard, "MAC", Now);
        Assert.Equal(NodeState.Online, sending.Other.State); // a change just arrived, so it is online
        Assert.Equal("Sending changes", sending.Other.Detail);
        Assert.Equal(LinkFlow.Up, sending.OtherFlow);

        var batch = Connected(new DeviceInfo("DESKTOP", true, Now)) with { WaitingFor = new PeerWait("DESKTOP", 340, 12) };
        Assert.Equal("Uploading 340 files", DeviceMap.Build(batch, "MAC", Now).Other.Detail);
    }

    [Fact]
    public void HistoryListsRecentDeletionsAndSearchesFiles()
    {
        var files = new[]
        {
            new ServerFile("Projects/report.docx", false, Now.AddHours(-1), 2048, new string('a', 64)),
            new ServerFile("Notes/todo.md", false, Now.AddDays(-3), 10),
            new ServerFile("Archive/old.txt", true, Now.AddDays(-2), 0),
            new ServerFile("Archive/ancient.txt", true, Now.AddDays(-45), 0),
        };
        var (deleted, total) = HistoryQuery.Filter(files, deleted: true, null, Now);
        Assert.Equal(["Archive/old.txt"], deleted.Select(f => f.Path));
        Assert.Equal(1, total);
        var (current, _) = HistoryQuery.Filter(files, deleted: false, null, Now);
        Assert.Equal(["Projects/report.docx", "Notes/todo.md"], current.Select(f => f.Path));
        var (found, _) = HistoryQuery.Filter(files, deleted: false, "REPORT", Now);
        Assert.Equal("report.docx", Assert.Single(found).Name);
        Assert.Equal("Projects", found[0].Folder);
        Assert.Equal("Top folder", new ServerFile("a.txt", false, Now, 1).Folder);

        Assert.Equal("No files were deleted in the last 30 days", HistoryQuery.Summary(0, 0, deleted: true, searching: false));
        Assert.Equal("Nothing matches your search", HistoryQuery.Summary(0, 0, deleted: false, searching: true));
        Assert.Equal("1 deleted file", HistoryQuery.Summary(1, 1, deleted: true, searching: false));
        Assert.Equal("Showing 400 of 1,204 files · search to find one", HistoryQuery.Summary(400, 1204, deleted: false, searching: false));

        var versions = new[]
        {
            new HistoryVersion("20261006T110000000Z-aaaaaaaa", Now.AddHours(-1), 2048, "aaaaaaaa"),
            new HistoryVersion("20261001T110000000Z-bbbbbbbb", Now.AddDays(-5), 1900, "bbbbbbbb"),
        };
        var rows = VersionRow.For(files[0], versions, Now);
        Assert.StartsWith("Today ", rows[0].Title);
        Assert.Equal("Replaced · 2 KB · same content as now", rows[0].Detail);
        Assert.Equal("Replaced · 1.86 KB", rows[1].Detail);
        Assert.StartsWith("Deleted · ", VersionRow.For(files[2], versions, Now)[0].Detail);
    }

    [Fact]
    public void ActivityIsGroupedByDayAndListsUpdateInPlace()
    {
        var local = Now.ToLocalTime();
        var today = new ActivityItem(local.AddMinutes(-1), ActivityKind.Uploaded, "a.txt", "Uploaded");
        var yesterday = new ActivityItem(local.AddDays(-1), ActivityKind.Downloaded, "b.txt", "Downloaded");
        var rows = ActivityDays.Rows([today, yesterday], Now);
        Assert.Equal(4, rows.Count);
        Assert.Equal("Today", Assert.IsType<ActivityDay>(rows[0]).Text);
        Assert.Same(today, rows[1]);
        Assert.Equal("Yesterday", Assert.IsType<ActivityDay>(rows[2]).Text);

        var shown = new ObservableCollection<object>(rows);
        var kept = shown[1];
        var newest = new ActivityItem(local, ActivityKind.Conflict, "c.txt", "Conflict");
        LiveLists.Sync(shown, ActivityDays.Rows([newest, today, yesterday], Now));
        Assert.Equal(5, shown.Count);
        Assert.Same(newest, shown[1]); // inserted under "Today"
        Assert.Same(kept, shown[2]);   // the existing row stayed (no re-animation)
        LiveLists.Sync(shown, ActivityDays.Rows([newest], Now));
        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public void MomentsAndDays()
    {
        var now = new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero).ToLocalTime();
        Assert.StartsWith("today ", Format.Moment(now.AddMinutes(-5), now));
        Assert.StartsWith("yesterday ", Format.Moment(now.AddDays(-1), now));
        Assert.DoesNotContain("2026", Format.Moment(now.AddDays(-20), now));
        Assert.Contains("2025", Format.Moment(now.AddDays(-400), now));
        Assert.Equal("Today", Format.Day(now.Date, now));
        Assert.Equal("Yesterday", Format.Day(now.Date.AddDays(-1), now));
        Assert.Contains("October", Format.Day(now.Date.AddDays(-3), now));
    }
}
