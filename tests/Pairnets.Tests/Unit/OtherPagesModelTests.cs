using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The models behind Activity (filters, search, folder rows), Devices (cards), History and Account.</summary>
public class OtherPagesModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static ActivityItem Item(ActivityKind kind, string? path, TimeSpan ago, string text = "x") =>
        new(Now - ago, kind, path, text);

    private static IReadOnlyList<ActivityItem> Sample() =>
    [
        Item(ActivityKind.Uploaded, "Reports/q3.xlsx", TimeSpan.FromMinutes(1)),
        Item(ActivityKind.Downloaded, "Photos/a.jpg", TimeSpan.FromMinutes(2)),
        Item(ActivityKind.Downloaded, "Photos/b.jpg", TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(10))),
        Item(ActivityKind.Downloaded, "Photos/c.jpg", TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(20))),
        Item(ActivityKind.Conflict, "Finance/budget (conflict).xlsx", TimeSpan.FromMinutes(5)),
        Item(ActivityKind.DeletedOnServer, "Drafts/old.docx", TimeSpan.FromMinutes(7)),
        Item(ActivityKind.Info, null, TimeSpan.FromMinutes(9), "Caught up"),
    ];

    [Fact]
    public void FilterCountsFollowTheItemsAndTheSearch()
    {
        var all = ActivityView.Counts(Sample());
        Assert.Equal(new ActivityCounts(7, 1, 3, 1, 1), all);
        Assert.Equal(3, all.Of(ActivityFilter.Downloaded));
        Assert.Equal(7, all.Of(ActivityFilter.All));

        var photos = ActivityView.Counts(Sample(), "photos");
        Assert.Equal(new ActivityCounts(3, 0, 3, 0, 0), photos);
    }

    [Fact]
    public void RowsFollowTheFilterAndTheSearchAndFoldFolders()
    {
        var rows = ActivityView.Rows(Sample(), ActivityFilter.All, null, Now);
        var day = Assert.IsType<ActivityDay>(rows[0]);
        Assert.Equal(7, day.Changes);
        Assert.Equal("7 changes", day.CountText);
        var folder = Assert.IsType<ActivityFolder>(rows[2]);
        Assert.Equal("Photos", folder.Folder);
        Assert.Equal("3 photos", folder.CountText);
        Assert.Equal("Downloaded", folder.Detail);

        var deleted = ActivityView.Rows(Sample(), ActivityFilter.Deleted, null, Now);
        Assert.Equal(2, deleted.Count); // the day heading and one file
        Assert.Equal("1 change", Assert.IsType<ActivityDay>(deleted[0]).CountText);

        var found = ActivityView.Rows(Sample(), ActivityFilter.All, "budget", Now);
        Assert.Equal(2, found.Count);
        Assert.True(ActivityView.NeedsReview(Assert.IsType<ActivityItem>(found[1])));
    }

    [Fact]
    public void AFolderRowNamesItsFolderAndWhereItIs()
    {
        var items = new[] { "Photos/Holiday 2026/1.jpg", "Photos/Holiday 2026/2.txt", "Photos/Holiday 2026/3.jpg" }
            .Select((p, i) => Item(ActivityKind.Uploaded, p, TimeSpan.FromSeconds(i))).ToList();
        var folder = Assert.IsType<ActivityFolder>(Assert.Single(ActivityGroups.Group(items)));
        Assert.Equal("Holiday 2026", folder.Name);
        Assert.Equal("Photos", folder.Where);
        Assert.Equal("3 files", folder.CountText); // one is not a picture
        Assert.Equal("Uploaded · Photos", folder.Detail);
    }

    [Fact]
    public void EmptyTextSaysWhy()
    {
        Assert.Equal("Nothing has synced yet", ActivityView.EmptyText([], ActivityFilter.All, null));
        Assert.Equal("Nothing matches \"zzz\"", ActivityView.EmptyText(Sample(), ActivityFilter.All, " zzz "));
        Assert.Equal("No conflicts files to show", ActivityView.EmptyText(Sample(), ActivityFilter.Conflicts, null));
    }

    private static DeviceInfo Device(string name, bool online, TimeSpan? seenAgo = null, string? icon = null) =>
        new(name, Now.AddDays(-30), Now - (seenAgo ?? TimeSpan.Zero), online, "1.0.48", "Windows", Now.AddHours(-1), "id-" + name, icon);

    [Fact]
    public void DeviceCardsListThisComputerFirstThenOnlineThenOffline()
    {
        var s = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "x", LastSyncAt = Now, Server = new ServerInfo("id", 1, 1, "1.0.48", 412L << 30, 1L << 40),
            DeviceIconsSupported = true,
            Devices = [Device("KITCHEN-PC", false, TimeSpan.FromDays(3)), Device("LAPTOP", true, icon: "tv"), Device("DESKTOP", true), Device("MEDIA-PC", false, TimeSpan.FromDays(1))],
        };

        var cards = DeviceCard.From(s, "DESKTOP", Now);

        Assert.Equal(["DESKTOP", "LAPTOP", "MEDIA-PC", "KITCHEN-PC"], cards.Select(c => c.Name));
        Assert.True(cards[0].IsThis);
        Assert.Equal("Online", cards[1].StatusText);
        Assert.Equal(DeviceIconKind.Tv, cards[1].Icon);
        Assert.True(cards[1].CanChooseIcon);
        Assert.Equal("Windows · 1.0.48", cards[1].SystemLine);
        Assert.Equal(DeviceCardTone.Offline, cards[2].Tone);
        Assert.Equal("Last seen yesterday", cards[2].StatusText);
        Assert.Equal("Last seen 3 days ago", cards[3].StatusText);
        Assert.Equal("4 use this nest · 2 online now", DevicesSummary.Line(cards));
    }

    [Fact]
    public void ANestWithoutIconsGuessesAndHidesThePencil()
    {
        var s = StatusSnapshot.Initial with { Devices = [Device("LAPTOP", true, icon: "tv"), Device("DESKTOP", true)] };
        var cards = DeviceCard.From(s, "DESKTOP", Now);
        Assert.All(cards, c => Assert.False(c.CanChooseIcon));
        Assert.Equal(DeviceIconKind.Laptop, cards.Single(c => c.Name == "LAPTOP").Icon);
    }

    [Fact]
    public void ACardShowsTheLiveSpeedOfAComputerThatMovesFiles()
    {
        var s = StatusSnapshot.Initial with
        {
            Devices = [Device("DESKTOP", true), Device("LAPTOP", true)],
            Peers = new Dictionary<string, PeerActivity>
            {
                ["LAPTOP"] = new(new TransferReport(0, 4.1 * (1 << 20), 3, 30, 0, 0, Downloading: true), Now, 0, null),
            },
        };
        var laptop = DeviceCard.From(s, "DESKTOP", Now).Single(c => c.Name == "LAPTOP");
        Assert.Equal(DeviceCardTone.Moving, laptop.Tone);
        Assert.Contains("MB/s", laptop.StatusNote);
    }

    [Fact]
    public void TheNestCardSaysVersionSpaceAndRetention()
    {
        var s = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, LastSyncAt = Now, NestUrl = "https://nest.example.com",
            Server = new ServerInfo("id", 1, 1, "1.0.48", 412L << 30, 1L << 40),
        };
        var card = NestCard.From(s, null);
        Assert.True(card.Online);
        Assert.Equal("nest.example.com", card.Address);
        Assert.Equal("1.0.48", card.Version);
        Assert.Equal("30 days", card.Keeps);
        Assert.StartsWith("412", card.FreeSpace);

        var none = NestCard.From(StatusSnapshot.Initial, null);
        Assert.Equal("Not connected", none.Address);
        Assert.Equal("–", none.Version);
    }

    [Fact]
    public void JoinRequestsAreNamedForTheChip()
    {
        Assert.Null(DevicesSummary.JoinText(StatusSnapshot.Initial));
        var one = StatusSnapshot.Initial with { JoinRequests = [new JoinRequest("RX4K-92TB", "PC-1", "Windows", Now)] };
        Assert.Equal("PC-1 is asking to join", DevicesSummary.JoinText(one));
        var two = StatusSnapshot.Initial with { JoinRequests = [.. one.JoinRequests, new JoinRequest("AAAA-BBBB", "PC-2", "Linux", Now)] };
        Assert.Equal("2 computers are asking to join", DevicesSummary.JoinText(two));
    }

    [Fact]
    public void HistoryRowsCountTheDaysLeftBeforeTheServerForgets()
    {
        var recent = new ServerFile("Drafts/old.docx", true, DateTimeOffset.Now.AddHours(-2), 0);
        Assert.Equal("30 days left", recent.DaysLeftText);
        Assert.False(recent.ExpiresSoon);
        var old = new ServerFile("Notes/old.txt", true, DateTimeOffset.Now.AddDays(-27).AddHours(-1), 0);
        Assert.Equal("3 days left", old.DaysLeftText);
        Assert.True(old.ExpiresSoon);
        var live = new ServerFile("Reports/q3.xlsx", false, DateTimeOffset.Now.AddMinutes(-3), 2_400_000);
        Assert.False(live.ExpiresSoon);
        Assert.StartsWith("Reports · ", live.FolderAndWhen);
        Assert.Equal("In Reports · 2.29 MB", live.InFolderText);
    }

    [Fact]
    public void TheAccountPageShowsTheEmailOrTheComputerNameOfAnOldNest()
    {
        var signedIn = StatusSnapshot.Initial with
        {
            NestUrl = "https://nest.example.com",
            Account = new AccountInfo("alex@example.com", "google", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)),
        };
        var page = AccountSummary.Build(signedIn, "DESKTOP", null);
        Assert.Equal("alex@example.com", page.Title);
        Assert.Equal("alex@example.com", page.Email);
        Assert.Equal("Google", page.MethodName);
        Assert.Equal("29 Sep 2026", page.AddedText);
        Assert.Equal("nest.example.com", page.NestHost);

        var old = AccountSummary.Build(StatusSnapshot.Initial with { NestUrl = "https://nest.example.com" }, "DESKTOP", null);
        Assert.Equal("DESKTOP", old.Title);
        Assert.Null(old.Email);
        Assert.Equal("D", old.Initial);
        Assert.Equal("–", old.MethodName);
    }

    [Fact]
    public void ANestReachedThroughSyncPairnetsAppIsNamedByThatAddressAndTheSignedInEmailIsShown()
    {
        // No website of its own and no "who am I" answer from the nest: the 1.0.55 local test showed "Not signed in to
        // a nest" and an address of "Not connected" here, next to a green Online.
        var s = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, LastSyncAt = Now, IsRelay = true,
            ServerUrl = "https://sync.pairnets.app/n/nst_0000example/",
            AccountEmail = "alex@example.com",
            Server = new ServerInfo("id", 1, 1, "1.0.55", 412L << 30, 1L << 40),
        };
        var page = AccountSummary.Build(s, "PC-1", null);
        Assert.Equal("sync.pairnets.app", page.NestHost);
        Assert.Equal("Signed in on sync.pairnets.app", page.Subtitle);
        Assert.Equal(("A", "alex@example.com", "alex@example.com"), (page.Initial, page.Title, page.Email));
        Assert.Equal("sync.pairnets.app", NestCard.From(s, null).Address);

        // The nest's own answer and website still come first.
        var answered = s with { NestUrl = "https://nest.example.com", Account = new AccountInfo("owner@example.com", "google", null) };
        Assert.Equal("nest.example.com", AccountSummary.Build(answered, "PC-1", null).NestHost);
        Assert.Equal("owner@example.com", AccountSummary.Build(answered, "PC-1", null).Email);
        Assert.Equal("nest.example.com", NestCard.From(answered, null).Address);
    }
}
