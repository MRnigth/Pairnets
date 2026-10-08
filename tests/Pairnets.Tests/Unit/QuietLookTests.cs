using System.Globalization;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The shared logic behind the Quiet look: folder rows in the activity lists, the notification switches,
/// the overview ring and the account summary.</summary>
public class QuietLookTests
{
    /// <summary>Noon on a fixed day in this computer's time zone, so day headings do not depend on where tests run.</summary>
    private static readonly DateTimeOffset Noon = new(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Local));

    private static ActivityItem Item(ActivityKind kind, string? path, DateTimeOffset at) => new(at, kind, path, path ?? "note");

    // ------------------------------------------------------------------ folder rows in the activity lists

    [Fact]
    public void ThreeOrMoreFilesOfOneFolderFoldIntoOneRow()
    {
        var items = new[]
        {
            Item(ActivityKind.Uploaded, "Photos/summer/c.jpg", Noon),
            Item(ActivityKind.Uploaded, "Photos/summer/b.jpg", Noon.AddSeconds(-50)),
            Item(ActivityKind.Uploaded, "Photos/summer/a.jpg", Noon.AddSeconds(-100)),
            Item(ActivityKind.Info, null, Noon.AddMinutes(-5)),
        };

        var rows = ActivityGroups.Group(items);

        Assert.Equal(2, rows.Count);
        var folder = Assert.IsType<ActivityFolder>(rows[0]);
        Assert.Equal(("Photos/summer", "3 files uploaded", Noon), (folder.Primary, folder.Secondary, folder.Time));
        Assert.Equal(items.Take(3), folder.Items);
        Assert.Same(items[3], rows[1]);
        Assert.Equal("2 files deleted on the server", new ActivityFolder("A", ActivityKind.DeletedOnServer, Noon, items.Take(2).ToList()).Secondary);
    }

    [Fact]
    public void ShortRunsMixedKindsAndOtherFoldersStayAsTheyAre()
    {
        var twoOnly = new[] { Item(ActivityKind.Uploaded, "A/1", Noon), Item(ActivityKind.Uploaded, "A/2", Noon) };
        Assert.Equal(twoOnly, ActivityGroups.Group(twoOnly));

        var mixedKinds = new[] { Item(ActivityKind.Uploaded, "A/1", Noon), Item(ActivityKind.Downloaded, "A/2", Noon), Item(ActivityKind.Uploaded, "A/3", Noon) };
        Assert.Equal(mixedKinds, ActivityGroups.Group(mixedKinds));

        var mixedFolders = new[] { Item(ActivityKind.Uploaded, "A/1", Noon), Item(ActivityKind.Uploaded, "B/2", Noon), Item(ActivityKind.Uploaded, "A/3", Noon) };
        Assert.Equal(mixedFolders, ActivityGroups.Group(mixedFolders));

        // Files at the top level have no folder to show; conflicts and warnings always stay one per row.
        var topLevel = new[] { Item(ActivityKind.Uploaded, "1", Noon), Item(ActivityKind.Uploaded, "2", Noon), Item(ActivityKind.Uploaded, "3", Noon) };
        Assert.Equal(topLevel, ActivityGroups.Group(topLevel));
        var conflicts = new[] { Item(ActivityKind.Conflict, "A/1", Noon), Item(ActivityKind.Conflict, "A/2", Noon), Item(ActivityKind.Conflict, "A/3", Noon) };
        Assert.Equal(conflicts, ActivityGroups.Group(conflicts));
    }

    [Fact]
    public void AGapOfMoreThanTwoMinutesBreaksARun()
    {
        var items = new[]
        {
            Item(ActivityKind.Downloaded, "A/5", Noon),
            Item(ActivityKind.Downloaded, "A/4", Noon.AddMinutes(-1)),
            Item(ActivityKind.Downloaded, "A/3", Noon.AddMinutes(-4)), // 3 minutes before the one above
            Item(ActivityKind.Downloaded, "A/2", Noon.AddMinutes(-5)),
            Item(ActivityKind.Downloaded, "A/1", Noon.AddMinutes(-7)), // 2 minutes exactly is still the same run
        };

        var rows = ActivityGroups.Group(items);

        Assert.Equal(3, rows.Count);
        Assert.Same(items[0], rows[0]);
        Assert.Same(items[1], rows[1]);
        Assert.Equal(items.Skip(2), Assert.IsType<ActivityFolder>(rows[2]).Items);
        Assert.Equal(2, ActivityGroups.Recent(items, 2).Count);
    }

    [Fact]
    public void ARunNeverCrossesADayHeading()
    {
        var midnight = Noon.AddHours(-12);
        var items = new[]
        {
            Item(ActivityKind.Uploaded, "A/4", Noon),
            Item(ActivityKind.Uploaded, "A/3", midnight.AddSeconds(30)),
            Item(ActivityKind.Uploaded, "A/2", midnight.AddSeconds(-30)),
            Item(ActivityKind.Uploaded, "A/1", midnight.AddSeconds(-60)),
            Item(ActivityKind.Uploaded, "B/3", midnight.AddHours(-1)),
            Item(ActivityKind.Uploaded, "B/2", midnight.AddHours(-1).AddSeconds(-10)),
            Item(ActivityKind.Uploaded, "B/1", midnight.AddHours(-1).AddSeconds(-20)),
        };

        var rows = ActivityDays.GroupedRows(items, Noon);

        Assert.Equal(7, rows.Count);
        Assert.Equal("Today", Assert.IsType<ActivityDay>(rows[0]).Text);
        Assert.Same(items[0], rows[1]);
        Assert.Same(items[1], rows[2]);
        Assert.Equal("Yesterday", Assert.IsType<ActivityDay>(rows[3]).Text);
        Assert.Same(items[2], rows[4]); // "A" has three files around midnight, but only two of them yesterday
        Assert.Same(items[3], rows[5]);
        Assert.Equal("B", Assert.IsType<ActivityFolder>(rows[6]).Folder);
    }

    // ------------------------------------------------------------------ notification switches

    [Theory]
    [InlineData("join:KQ7M-4PXD", NoticeKind.JoinRequest)]
    [InlineData("update:1.0.90", NoticeKind.Update)]
    [InlineData("server-updated", NoticeKind.Update)]
    [InlineData("catchup", NoticeKind.Info)]
    [InlineData("conflict:a.txt", NoticeKind.Attention)]
    [InlineData("warning:a.txt", NoticeKind.Attention)]
    [InlineData("blocked:MassDelete", NoticeKind.Attention)]
    [InlineData("auth", NoticeKind.Attention)]
    [InlineData("token", NoticeKind.Attention)]
    [InlineData("disk", NoticeKind.Attention)]
    public void EachNotificationHasItsKind(string key, NoticeKind kind) => Assert.Equal(kind, NotifyPolicy.KindOf(key));

    [Fact]
    public void EachSwitchTurnsOffOnlyItsOwnNotifications()
    {
        string[] keys = ["join:KQ7M-4PXD", "conflict:a.txt", "update:1.0.90", "server-updated", "catchup"];
        Assert.All(keys, k => Assert.True(NotifyPolicy.Allows(new ClientSettings(), k)));

        var noJoins = new ClientSettings { NotifyJoinRequests = false };
        Assert.Equal([false, true, true, true, true], keys.Select(k => NotifyPolicy.Allows(noJoins, k)));
        var noAttention = new ClientSettings { NotifyAttention = false };
        Assert.Equal([true, false, true, true, true], keys.Select(k => NotifyPolicy.Allows(noAttention, k)));
        var noUpdates = new ClientSettings { NotifyUpdates = false };
        Assert.Equal([true, true, false, false, true], keys.Select(k => NotifyPolicy.Allows(noUpdates, k)));
        var none = new ClientSettings { NotifyJoinRequests = false, NotifyAttention = false, NotifyUpdates = false };
        Assert.True(NotifyPolicy.Allows(none, "catchup")); // plain news is always shown
    }

    // ------------------------------------------------------------------ the overview ring

    private static StatusSnapshot Status(RunnerStatus status) => StatusSnapshot.Initial with { Status = status, Text = status.ToString() };

    [Theory]
    [InlineData(RunnerStatus.Idle, RingKind.Done, "S.Green", "I.Check")]
    [InlineData(RunnerStatus.Blocked, RingKind.Decision, "S.Orange", "I.Bang")]
    [InlineData(RunnerStatus.Paused, RingKind.Paused, "S.Grey", "I.Pause")]
    [InlineData(RunnerStatus.Offline, RingKind.Offline, "S.Grey", "I.CloudOff")]
    [InlineData(RunnerStatus.Error, RingKind.Problem, "S.Red", "I.X")]
    public void EachStatusHasItsRing(RunnerStatus status, RingKind kind, string brush, string icon)
    {
        var ring = OverviewRing.From(Status(status));
        Assert.Equal(new OverviewRing(kind, 100, null, null, brush, icon), ring);
        Assert.False(ring.Spins);
        Assert.False(ring.Breathes);
    }

    [Fact]
    public void ASyncShowsItsPercentOrSpins()
    {
        var known = OverviewRing.From(Status(RunnerStatus.Syncing) with { PassBytesDone = 50, PassBytesTotal = 200, FilesTotal = 4 });
        Assert.Equal(new OverviewRing(RingKind.Syncing, 25, "25%", null, "S.Blue", null), known);
        Assert.False(known.Spins);

        var unknown = OverviewRing.From(Status(RunnerStatus.Syncing));
        Assert.Equal((RingKind.Syncing, (string?)null, "I.Sync"), (unknown.Kind, unknown.CenterText, unknown.IconKey));
        Assert.True(unknown.Spins);
    }

    [Fact]
    public void WaitingForTheOtherComputerBreathes()
    {
        var waiting = OverviewRing.From(Status(RunnerStatus.Idle) with { WaitingFor = new PeerWait("DESKTOP", 340, 85) });
        Assert.Equal(new OverviewRing(RingKind.Waiting, 25, "25%", "on the server", "S.Grey", null), waiting);
        Assert.True(waiting.Breathes);

        var noCount = OverviewRing.From(Status(RunnerStatus.Idle) with { WaitingFor = new PeerWait("DESKTOP", 0, 0) });
        Assert.Equal(new OverviewRing(RingKind.Waiting, 0, null, null, "S.Grey", "I.Wait"), noCount);

        // Something of this computer's own moving wins over the wait.
        var busy = Status(RunnerStatus.Syncing) with { WaitingFor = new PeerWait("DESKTOP", 340, 85), CurrentPath = "a.txt" };
        Assert.Equal(RingKind.Syncing, OverviewRing.From(busy).Kind);
    }

    // ------------------------------------------------------------------ the account summary

    private static readonly ServerInfo Server = new("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);

    private static string AddedText(DateTimeOffset at) =>
        "Added " + at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) + " · Shown in conflict file names";

    [Fact]
    public void AnAccountShowsTheOwnersEmailAndHowTheySignedIn()
    {
        var s = Status(RunnerStatus.Idle) with
        {
            NestUrl = "https://nest.example.com",
            Server = Server,
            LastSyncAt = Noon,
            Account = new AccountInfo("anna@example.com", "google", Noon.AddDays(-3)),
        };

        var summary = AccountSummary.Build(s, "MAC", "http://192.0.2.4:5075/");

        Assert.Equal(("A", "anna@example.com", "Signed in with Google"), (summary.Initial, summary.Title, summary.Subtitle));
        Assert.Equal("nest.example.com", summary.NestHost); // the nest's website wins over the address in the settings
        Assert.Equal("Pairnets server 1.0.58 · 412 GB free · Connected", summary.NestDetail);
        Assert.Equal(("MAC", AddedText(Noon.AddDays(-3))), (summary.DeviceName, summary.DeviceDetail));
    }

    [Fact]
    public void WhoLetTheComputerInTravelsBothWaysBetweenOldAndNewVersions()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<DeviceMe>("""{"id":"d1","name":"MAC","kind":"device-key"}""", PairnetsJson.Options)!;
        Assert.Equal(new DeviceMe("d1", "MAC", DeviceMe.KindDeviceKey), old);
        Assert.Null(old.Email);

        var added = new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        var json = System.Text.Json.JsonSerializer.Serialize(new DeviceMe("d1", "MAC", DeviceMe.KindDeviceKey, "anna@example.com", "google", added), PairnetsJson.Options);
        Assert.Contains("\"method\":\"google\"", json);
        Assert.Equal(added, System.Text.Json.JsonSerializer.Deserialize<DeviceMe>(json, PairnetsJson.Options)!.Added);
    }

    [Theory]
    [InlineData("google", "Signed in with Google")]
    [InlineData("email", "Signed in with email")]
    [InlineData(" Passkey ", "Signed in with a passkey")]
    [InlineData("password", "Signed in with a password")]
    [InlineData("setup link", null)]
    [InlineData(null, null)]
    public void TheWayOfSigningInIsSaidPlainly(string? method, string? text) => Assert.Equal(text, new AccountInfo(null, method, null).MethodText);

    [Fact]
    public void WithoutAnAccountTheComputerIsTheTitle()
    {
        var s = Status(RunnerStatus.Idle) with
        {
            Devices = [new DeviceInfo("mac", Noon.AddDays(-30), Noon, true)],
            Account = new AccountInfo(null, "setup link", null), // a way the apps do not name
        };

        var summary = AccountSummary.Build(s, "  MAC ", "http://192.0.2.4:5075/");

        Assert.Equal(("M", "MAC", "Signed in on 192.0.2.4:5075"), (summary.Initial, summary.Title, summary.Subtitle));
        Assert.Equal("192.0.2.4:5075", summary.NestHost);
        Assert.Equal(AddedText(Noon.AddDays(-30)), summary.DeviceDetail); // from the computers list
        Assert.Equal(string.Empty, summary.NestDetail);

        var nothing = AccountSummary.Build(StatusSnapshot.Initial, null, null);
        Assert.Equal(("T", "This computer", "Not signed in to a nest"), (nothing.Initial, nothing.Title, nothing.Subtitle));
        Assert.Null(nothing.NestHost);
        Assert.Equal("Shown in conflict file names", nothing.DeviceDetail);
        Assert.Equal("?", AccountSummary.Build(StatusSnapshot.Initial, "--", null).Initial);
        Assert.Equal("7", AccountSummary.Build(StatusSnapshot.Initial with { Account = new("_7anna@example.com", null, null) }, "MAC", null).Initial);
    }
}
