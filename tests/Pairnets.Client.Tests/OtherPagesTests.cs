using System.Windows;
using System.Windows.Controls;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Tests;

/// <summary>
/// The pages of the main window that the new look rebuilt (Activity, Needs you, History, Devices, Account): they open,
/// draw what the models in Core say, and ask the app for what their buttons are for. The click-everything tests press
/// every button; these check what the pages show and what a press changes.
/// </summary>
public sealed class OtherPagesTests
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private static readonly DateTimeOffset Utc = DateTimeOffset.UtcNow;

    private static readonly IReadOnlyList<DeviceInfo> SixComputers =
    [
        new("DESKTOP", Utc.AddDays(-30), Utc, true, "1.0.48", "Windows"),
        new("LAPTOP", Utc.AddDays(-30), Utc, true, "1.0.48", "Windows"),
        new("MINI-PC", Utc.AddDays(-30), Utc, true, "1.0.48", "Linux"),
        new("MEDIA-PC", Utc.AddDays(-30), Utc.AddDays(-2), false, "1.0.48", "Linux"),
        new("OFFICE-PC", Utc.AddDays(-30), Utc.AddDays(-3), false, "1.0.48", "Windows"),
        new("KITCHEN-PC", Utc.AddDays(-30), Utc.AddDays(-4), false, "1.0.48", "Windows"),
    ];

    private static async Task<(MainWindow Window, RecordingActions Actions, SampleHistory History)> OpenAsync()
    {
        var history = new SampleHistory();
        var actions = new RecordingActions(history);
        var window = await Wpf.Ui(() =>
        {
            var w = new MainWindow(actions) { Width = 1100, Height = 760 };
            w.Show();
            return w;
        });
        await Wpf.WaitUntil(() => window.IsLoaded, "the window to open");
        return (window, actions, history);
    }

    private static async Task Done(MainWindow window)
    {
        await Wpf.Settle();
        Assert.Empty(Wpf.TakeCrashes());
        await Wpf.CloseAllWindows();
    }

    private static IReadOnlyList<ActivityItem> Items()
    {
        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(Now.AddHours(-5), ActivityKind.Downloaded, "Notes/ideas.md", "Downloaded"));
        feed.Add(new ActivityItem(Now.AddHours(-4), ActivityKind.DeletedHere, "Archive/old.txt", "Deleted"));
        feed.Add(new ActivityItem(Now.AddHours(-3), ActivityKind.Conflict, "Finance/budget.xlsx", "Conflict"));
        for (var i = 0; i < 4; i++)
            feed.Add(new ActivityItem(Now.AddMinutes(-30).AddSeconds(i * 4), ActivityKind.Uploaded, $"Photos/summer/IMG_{i}.jpg", "Uploaded"));
        return feed.Items;
    }

    // ------------------------------------------------------------------ the pages open without the shared header

    [Fact]
    public async Task EveryPageOpensAndDrawsItsOwnHeader()
    {
        var (window, _, _) = await OpenAsync();
        foreach (var page in new[] { MainPage.Activity, MainPage.Attention, MainPage.History, MainPage.Devices, MainPage.Account })
        {
            await Wpf.Ui(() => window.Navigate(page));
            await Wpf.Settle();
            Assert.Equal(page, window.Page);
            Assert.False(await Wpf.Ui(() => window.PageHeader.IsVisible), $"The shared header row shows on {page}: the page draws its own.");
        }
        await Wpf.Ui(() => window.Navigate(MainPage.Overview));
        Assert.True(await Wpf.Ui(() => window.PageHeader.IsVisible), "The Overview keeps the shared header row.");
        await Done(window);
    }

    [Fact]
    public async Task TheAccountPageChecksNoTile_AndATileTakesYouBack()
    {
        var (window, actions, _) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.Account));
        Assert.True(await Wpf.Ui(() => new[] { window.NavOverview, window.NavActivity, window.NavAttention, window.NavHistory, window.NavDevices, window.NavSettings }.All(t => t.IsChecked != true)));
        Assert.True(await Wpf.Ui(() => new[] { window.DockOverview, window.DockActivity, window.DockAttention, window.DockHistory, window.DockDevices, window.DockSettings }.All(t => t.IsChecked != true)));
        await Wpf.Ui(() => window.NavActivity.IsChecked = true);
        await Wpf.Settle();
        Assert.Equal(MainPage.Activity, window.Page);
        Assert.True(await Wpf.Ui(() => window.DockActivity.IsChecked == true));
        Assert.DoesNotContain("ShowSettings", actions.Calls.Items);
        await Done(window);
    }

    // ------------------------------------------------------------------ screen readers

    [Fact]
    public async Task ThePauseButtonIsReadOutAsPauseThenResume()
    {
        var (window, _, _) = await OpenAsync();
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date" };
        await Wpf.Ui(() => window.ShowStatus(idle, @"D:\Sync\Work", "DESKTOP"));
        await Wpf.Settle();
        Assert.Equal("Pause", await Wpf.Ui(() => SpokenName(window, "Pause")));

        await Wpf.Ui(() => window.ShowStatus(idle with { Status = RunnerStatus.Paused, Paused = true }, @"D:\Sync\Work", "DESKTOP"));
        await Wpf.Settle();
        Assert.Equal("Resume", await Wpf.Ui(() => SpokenName(window, "Resume")));
        await Done(window);
    }

    /// <summary>What a screen reader reads out for the button that shows <paramref name="label"/>.</summary>
    private static string SpokenName(Window window, string label) =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(
            Presser.Find(window).First(t => t.Label == label && t.Element is Button).Element).GetName();

    // ------------------------------------------------------------------ Activity

    [Fact]
    public async Task ActivityFilterPills_ChangeTheRowsAndShowCounts()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowActivity(Items());
            window.Navigate(MainPage.Activity);
        });
        var page = window.ActivityPane;
        Assert.Equal("7", await Wpf.Ui(() => page.CountOf(ActivityFilter.All)));
        Assert.Equal("4", await Wpf.Ui(() => page.CountOf(ActivityFilter.Uploaded)));
        Assert.Equal("1", await Wpf.Ui(() => page.CountOf(ActivityFilter.Conflicts)));
        Assert.Contains(await Wpf.Ui(() => page.ShownRows.ToList()), r => r is ActivityFolder { Items.Count: 4 });

        await Wpf.Ui(() => page.Filter = ActivityFilter.Conflicts);
        var rows = await Wpf.Ui(() => page.ShownRows.OfType<ActivityItem>().ToList());
        Assert.Equal("Finance/budget.xlsx", Assert.Single(rows).Path);

        await Wpf.Ui(() => page.Filter = ActivityFilter.Deleted);
        Assert.Equal("Archive/old.txt", Assert.Single(await Wpf.Ui(() => page.ShownRows.OfType<ActivityItem>().ToList())).Path);

        // A search narrows the counts too, and a filter or search with no match says so inside the list.
        await Wpf.Ui(() =>
        {
            page.Filter = ActivityFilter.All;
            page.SearchText = "ideas";
        });
        Assert.Equal("1", await Wpf.Ui(() => page.CountOf(ActivityFilter.All)));
        Assert.Equal("0", await Wpf.Ui(() => page.CountOf(ActivityFilter.Uploaded)));
        await Wpf.Ui(() => page.SearchText = "nothing like this");
        Assert.Equal("Nothing matches \"nothing like this\"", await Wpf.Ui(() => page.NoMatchMessage));
        Assert.False(await Wpf.Ui(() => page.ShowsEmptyState));
        await Done(window);
    }

    [Fact]
    public async Task ActivityWithNothingSynced_ShowsThePictureAndTheFolder()
    {
        var (window, actions, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date" }, @"D:\Sync\Work", "DESKTOP");
            window.ShowActivity([]);
            window.Navigate(MainPage.Activity);
        });
        Assert.True(await Wpf.Ui(() => window.ActivityPane.ShowsEmptyState));
        Assert.False(await Wpf.Ui(() => window.ActivityPane.FilterBar.IsVisible));
        Assert.Contains(@"D:\Sync\Work", await Wpf.Ui(() => window.ActivityPane.EmptyText.Text));
        await Wpf.Settle(); // layout of the page just opened
        var open = await Wpf.Ui(() => Presser.Find(window).Single(t => t.Label == "Open your folder"));
        await Wpf.Ui(() => Presser.Press(open));
        await Wpf.Settle();
        Assert.Contains("OpenFolder", actions.Calls.Items);
        await Done(window);
    }

    [Fact]
    public async Task AFolderRowOpensToTwoFilesThenShowsAll()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowActivity(Items());
            window.Navigate(MainPage.Activity);
        });
        await Wpf.Settle(); // layout of the page just opened
        string[] Names() => [.. Presser.Elements(window.ActivityPane).OfType<TextBlock>().Select(t => t.Text).Where(t => t.StartsWith("IMG_", StringComparison.Ordinal))];
        Assert.Empty(await Wpf.Ui(Names));
        await Wpf.Settle(); // layout of the page just opened
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Label.StartsWith("Show the 4 photos", StringComparison.Ordinal))));
        await Wpf.Settle();
        Assert.Equal(2, (await Wpf.Ui(Names)).Length);
        await Wpf.Settle(); // layout of the page just opened
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Label == "Show all 4 photos")));
        await Wpf.Settle();
        Assert.Equal(4, (await Wpf.Ui(Names)).Length);
        await Done(window);
    }

    [Fact]
    public async Task ReviewOnAConflictRow_OpensNeedsYou()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowActivity(Items());
            window.Navigate(MainPage.Activity);
        });
        await Wpf.Settle(); // layout of the page just opened
        var review = await Wpf.Ui(() => Presser.Find(window).Where(t => t.Label == "Review").ToList());
        await Wpf.Settle(); // layout of the page just opened
        Assert.True(review.Count > 0, "Buttons: " + string.Join(", ", await Wpf.Ui(() => Presser.Find(window).Select(t => t.Label).ToList())));
        await Wpf.Ui(() => Presser.Press(review[0]));
        await Wpf.Settle();
        Assert.Equal(MainPage.Attention, window.Page);
        await Done(window);
    }

    // ------------------------------------------------------------------ Needs you

    [Fact]
    public async Task NeedsYou_OneCardForEachThing_ByKind()
    {
        var (window, actions, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowAttention(
            [
                new("PC-1 (Windows) wants to join", "Code RX4K-92TB.", "Review on your nest", () => actions.Calls.Add("join"), AttentionKind.Join, "RX4K-92TB", Now),
                new("Conflict copy: budget.xlsx", "Both computers changed this file.", "Show in folder", () => actions.Calls.Add("conflict"), AttentionKind.Conflict, null, Now),
                new("Docs/Report.TXT", "Same name, other capitals.", "Show in folder", () => actions.Calls.Add("clash"), AttentionKind.NameClash),
            ]);
            window.Navigate(MainPage.Attention);
        });
        var page = window.NeedsPane;
        Assert.Equal(3, await Wpf.Ui(() => page.CardCount));
        Assert.Equal("3 things only you can decide. Everything else keeps syncing meanwhile.", await Wpf.Ui(() => page.SubtitleText));
        await Wpf.Settle(); // layout of the page just opened
        var texts = await Wpf.Ui(() => Presser.Elements(page).OfType<TextBlock>().Select(t => t.Text).ToList());
        Assert.Contains("Code on PC-1", texts);
        Assert.Contains(texts, t => t.Replace("\u2009", string.Empty) == "RX4K-92TB");
        Assert.Contains("Aa", texts);
        // No Allow or Deny buttons: the nest decides, the button opens its review page.
        await Wpf.Settle(); // layout of the page just opened
        var labels = await Wpf.Ui(() => Presser.Find(window).Select(t => t.Label).ToList());
        Assert.DoesNotContain("Allow", labels);
        Assert.DoesNotContain("Deny", labels);
        await Wpf.Settle(); // layout of the page just opened
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Label == "Review on your nest")));
        await Wpf.Settle();
        Assert.Contains("join", actions.Calls.Items);

        await Wpf.Ui(() => window.ShowAttention([]));
        Assert.True(await Wpf.Ui(() => page.ShowsCalmState));
        Assert.Equal(0, await Wpf.Ui(() => page.CardCount));
        await Done(window);
    }

    [Fact]
    public async Task NeedsYou_TheOfferOfThisComputersOwnName_HasBothChoices()
    {
        var (window, actions, _) = await OpenAsync();
        var offer = new NameOffer("PC-1", "StudyPC");
        await Wpf.Ui(() =>
        {
            window.ShowAttention([new(offer.Title, offer.Detail, offer.UseLabel, () => actions.Calls.Add("use"), AttentionKind.Rename,
                SecondLabel: offer.KeepLabel, Second: () => actions.Calls.Add("keep"))]);
            window.Navigate(MainPage.Attention);
        });
        var page = window.NeedsPane;
        Assert.Equal(1, await Wpf.Ui(() => page.CardCount));
        await Wpf.Settle(); // layout of the page just opened
        var texts = await Wpf.Ui(() => Presser.Elements(page).OfType<TextBlock>().Select(t => t.Text).ToList());
        Assert.Contains("Use this computer's own name?", texts);
        Assert.Contains(texts, t => t.Contains("called PC-1", StringComparison.Ordinal));
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Label == "Keep PC-1")));
        await Wpf.Settle();
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Label == "Use StudyPC")));
        await Wpf.Settle();
        Assert.Equal(["keep", "use"], actions.Calls.Items.Where(c => c is "keep" or "use"));
        await Done(window);
    }

    // ------------------------------------------------------------------ History

    [Fact]
    public async Task HistoryRestoreFromTheDeletedTab_BringsBackTheNewestCopy()
    {
        var (window, actions, history) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.History));
        var page = window.HistoryPane;
        await Wpf.WaitUntil(() => page.DeletedRows.Count == 3, "the three deleted files");
        Assert.True(await Wpf.Ui(() => page.DeletedTab.IsChecked == true));
        Assert.Equal("3", await Wpf.Ui(() => page.DeletedCount.Text));

        await Wpf.Settle(); // layout of the page just opened
        var restore = await Wpf.Ui(() => Presser.Find(window).First(t => t.Element is Button { CommandParameter: ServerFile }));
        var file = await Wpf.Ui(() => (ServerFile)((Button)restore.Element).CommandParameter);
        await Wpf.Ui(() => Presser.Press(restore));
        await Wpf.WaitUntil(() => page.RestoreMessage.IsVisible && page.RestoreText.StartsWith("Restored.", StringComparison.Ordinal), "the restore to be reported");
        Assert.Contains((file.Path, "20261006T100000000Z-c0ffee00"), history.Restored.Items);
        Assert.Equal(MainPage.History, window.Page);
        await Done(window);
    }

    [Fact]
    public async Task ShowVersionsOnAnActivityRow_OpensHistoryOnThatFileInAllFiles()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() => window.ShowVersionsFor("Projects/report.docx"));
        var page = window.HistoryPane;
        await Wpf.WaitUntil(() => page.VersionRowCount == 2, "the versions of report.docx");
        Assert.Equal(MainPage.History, window.Page);
        Assert.True(await Wpf.Ui(() => page.FilesTab.IsChecked == true));
        Assert.Equal("report.docx", await Wpf.Ui(() => page.DetailName.Text));
        await Done(window);
    }

    [Fact]
    public async Task HistoryHasTheSearchPlaceholderOfItsTab()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.History));
        await Wpf.WaitUntil(() => window.HistoryPane.DeletedRows.Count > 0, "the files");
        Assert.Equal("Search deleted files by name or folder", await Wpf.Ui(() => window.HistoryPane.HistorySearch.Placeholder));
        await Wpf.Ui(() => window.HistoryPane.ShowAllFiles());
        Assert.Equal("Search all files by name or folder", await Wpf.Ui(() => window.HistoryPane.HistorySearch.Placeholder));
        await Done(window);
    }

    // ------------------------------------------------------------------ Devices

    [Fact]
    public async Task DevicesShowsTheNestAndACardForEachComputer()
    {
        var (window, actions, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowStatus(StatusSnapshot.Initial with
            {
                Status = RunnerStatus.Idle, Text = "Up to date", Server = new ServerInfo("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40),
                Devices = SixComputers, NestUrl = "https://nest.example.com",
                JoinRequests = [new JoinRequest("RX4K-92TB", "PC-1", "Windows", Utc)],
            }, @"D:\Sync\Work", "DESKTOP");
            window.Navigate(MainPage.Devices);
        });
        var page = window.DevicesPane;
        Assert.Equal(6, await Wpf.Ui(() => page.CardCount));
        Assert.Equal("nest.example.com", await Wpf.Ui(() => page.NestAddressText));
        Assert.Equal("6 use this nest · 3 online now", await Wpf.Ui(() => page.SummaryText));
        Assert.Equal("PC-1 is asking to join", await Wpf.Ui(() => page.JoinChipText));
        var cards = await Wpf.Ui(() => page.Cards.ToList());
        Assert.Equal("DESKTOP", await Wpf.Ui(() => cards[0].NameLabel));
        Assert.True(await Wpf.Ui(() => cards[0].ShowsThisPill));
        Assert.StartsWith("Last seen", await Wpf.Ui(() => cards[^1].StatusLine), StringComparison.Ordinal);
        Assert.False(await Wpf.Ui(() => cards.Any(c => c.PencilShows)), "The pencil stays hidden on a nest that cannot save icons.");

        await Wpf.Settle(); // layout of the page just opened
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Element.Name == "JoinChip")));
        await Wpf.Settle();
        Assert.Equal(MainPage.Attention, window.Page);
        Assert.DoesNotContain("ManageDevices", actions.Calls.Items);
        await Done(window);
    }

    [Fact]
    public async Task DevicesWithoutAList_SaysWhatIsHappening()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.Devices));
        Assert.Equal("Loading the computers on your nest…", await Wpf.Ui(() => window.DevicesPane.EmptyText));
        await Wpf.Ui(() => window.ShowStatus(StatusSnapshot.Initial with { DevicesUnsupported = true }, null, "DESKTOP"));
        Assert.Equal("Update your nest to see the computers that use it.", await Wpf.Ui(() => window.DevicesPane.EmptyText));
        await Done(window);
    }

    // ------------------------------------------------------------------ Account

    [Fact]
    public async Task AccountPage_ShowsTheEmail_AndTheFacts()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowStatus(StatusSnapshot.Initial with
            {
                Status = RunnerStatus.Idle, Text = "Up to date", Devices = SixComputers, NestUrl = "https://nest.example.com",
                Account = new AccountInfo("alex@example.com", "google", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)),
            }, @"D:\Sync\Work", "DESKTOP");
            window.AccountButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        await Wpf.Settle();
        var page = window.AccountPane;
        Assert.Equal(MainPage.Account, window.Page);
        Assert.Equal("alex@example.com", await Wpf.Ui(() => page.AccountTitleText));
        Assert.Equal("A", await Wpf.Ui(() => page.InitialText));
        Assert.Equal("Google", await Wpf.Ui(() => page.MethodText));
        Assert.Equal("DESKTOP", await Wpf.Ui(() => page.DeviceText));
        Assert.Equal("nest.example.com", await Wpf.Ui(() => page.NestText));
        Assert.Contains("2026", await Wpf.Ui(() => page.AddedText));
        await Done(window);
    }

    [Fact]
    public async Task AccountPage_OnAnOlderNest_ShowsTheComputerName()
    {
        var (window, _, _) = await OpenAsync();
        await Wpf.Ui(() =>
        {
            window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", Devices = SixComputers, NestUrl = "https://nest.example.com" },
                @"D:\Sync\Work", "DESKTOP");
            window.Navigate(MainPage.Account);
        });
        var page = window.AccountPane;
        Assert.Equal("DESKTOP", await Wpf.Ui(() => page.AccountTitleText));
        Assert.Equal("D", await Wpf.Ui(() => page.InitialText));
        Assert.Equal("–", await Wpf.Ui(() => page.MethodText));
        await Done(window);
    }

    [Fact]
    public async Task AccountSwitches_CallSetNotifiesAtOnce_AndReadTheSavedChoice()
    {
        var (window, actions, _) = await OpenAsync();
        actions.SetNotifies(NoticeKind.Update, false);
        actions.Calls.Clear();
        await Wpf.Ui(() => window.Navigate(MainPage.Account));
        var page = window.AccountPane;
        Assert.True(await Wpf.Ui(() => page.NotifySwitch(NoticeKind.Attention).IsChecked == true));
        Assert.True(await Wpf.Ui(() => page.NotifySwitch(NoticeKind.Update).IsChecked == false), "A switch that is off in the settings shows off.");
        Assert.Empty(actions.Calls.Items); // reading the choices is not a change

        await Wpf.Ui(() => page.NotifySwitch(NoticeKind.JoinRequest).IsChecked = false);
        await Wpf.Settle();
        Assert.Equal(["SetNotifies(JoinRequest,False)"], actions.Calls.Items);
        await Wpf.Ui(() => page.NotifySwitch(NoticeKind.Update).IsChecked = true);
        await Wpf.Settle();
        Assert.Contains("SetNotifies(Update,True)", actions.Calls.Items);

        // The two switches the design drew that nothing in the app honours are not drawn.
        await Wpf.Settle(); // layout of the page just opened
        var texts = await Wpf.Ui(() => Presser.Elements(page).OfType<TextBlock>().Select(t => t.Text).ToList());
        Assert.DoesNotContain("When a big sync finishes", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("When the nest can", StringComparison.Ordinal));
        await Done(window);
    }

    [Fact]
    public async Task AccountButtons_ManageAndSignOut_AskTheApp()
    {
        var (window, actions, _) = await OpenAsync();
        await Wpf.Ui(() => window.Navigate(MainPage.Account));
        await Wpf.Settle(); // layout of the page just opened
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Label == "Manage your account on sync.pairnets.app")));
        await Wpf.Settle(); // layout of the page just opened
        await Wpf.Ui(() => Presser.Press(Presser.Find(window).Single(t => t.Label == "Sign out")));
        await Wpf.Settle();
        Assert.Contains("OpenAccountSite", actions.Calls.Items);
        Assert.Contains("SignOut", actions.Calls.Items);
        await Done(window);
    }
}
