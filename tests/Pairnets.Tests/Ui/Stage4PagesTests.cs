using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>The pages of the "new look" stage 4 in the Mac/Linux app: Activity, Needs you, History, Devices, Settings and Account.</summary>
public class Stage4PagesTests
{
    private static ActivityFeed SampleFeed(DateTimeOffset now)
    {
        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(now.AddMinutes(-60), ActivityKind.Uploaded, "Reports/q3.xlsx", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-50), ActivityKind.Downloaded, "Notes/ideas.md", "Downloaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-40), ActivityKind.DeletedHere, "Drafts/old.docx", "Deleted"));
        feed.Add(new ActivityItem(now.AddMinutes(-30), ActivityKind.Conflict, "Finance/budget (conflict LAPTOP 2026-10-02 141509).xlsx", "Conflict"));
        feed.Add(new ActivityItem(now.AddMinutes(-20), ActivityKind.Uploaded, "Notes/meeting.md", "Uploaded"));
        return feed;
    }

    private static MainWindow Open(SampleActions? actions = null)
    {
        var window = new MainWindow(actions) { Width = 1100, Height = 780 };
        window.Show();
        return window;
    }

    /// <summary>Lets the page that was just opened build and measure its controls, as the screen does.</summary>
    private static void Layout(MainWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Close(MainWindow window)
    {
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void EveryPageDrawsItsOwnHeaderAndTheOverviewKeepsTheSharedOne()
    {
        var window = Open();
        Assert.True(window.HeaderRowShown);
        foreach (var page in new[] { MainPage.Activity, MainPage.Attention, MainPage.History, MainPage.Devices, MainPage.Account })
        {
            window.Navigate(page);
            Assert.False(window.HeaderRowShown, $"{page} has its own header");
        }
        window.Navigate(MainPage.Overview);
        Assert.True(window.HeaderRowShown);
        Close(window);
    }

    [AvaloniaFact]
    public void ActivityFilterPillsChangeTheRowsAndTheCountsFollowTheSearch()
    {
        var window = Open();
        window.ShowActivity(SampleFeed(DateTimeOffset.Now).Items);
        window.Navigate(MainPage.Activity);
        Assert.Equal(6, window.ActivityRowCount); // one day heading and five files
        Assert.Equal("All 5", window.ActivityPillText(ActivityFilter.All));
        Assert.Equal("Uploaded 2", window.ActivityPillText(ActivityFilter.Uploaded));
        Assert.Equal("Conflicts 1", window.ActivityPillText(ActivityFilter.Conflicts));

        window.SetActivityFilter(ActivityFilter.Uploaded);
        Assert.Equal(3, window.ActivityRowCount);
        window.SetActivityFilter(ActivityFilter.Conflicts);
        Assert.Equal(2, window.ActivityRowCount);
        window.SetActivityFilter(ActivityFilter.Deleted);
        Assert.Equal(2, window.ActivityRowCount);

        window.SetActivityFilter(ActivityFilter.All);
        window.SearchActivity("notes");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, window.ActivityRowCount);
        Assert.Equal("Uploaded 1", window.ActivityPillText(ActivityFilter.Uploaded)); // counts follow the search
        window.SearchActivity("nothing like this");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, window.ActivityRowCount);
        Assert.Equal("Nothing matches \"nothing like this\"", window.ActivityNoMatchValue);
        Close(window);
    }

    [AvaloniaFact]
    public void ActivityWithNothingSyncedShowsTheFolderAndOpensIt()
    {
        var actions = new SampleActions();
        var window = Open(actions);
        window.ShowStatus(StatusSnapshot.Initial, "/home/me/Work", "DESKTOP");
        window.Navigate(MainPage.Activity);
        Assert.True(window.ActivityEmptyShown);
        Assert.Equal("Put a file in /home/me/Work and it shows up here as soon as it is safe on your nest.", window.ActivityEmptyValue);
        window.ShowActivity(SampleFeed(DateTimeOffset.Now).Items);
        Assert.False(window.ActivityEmptyShown);
        Close(window);
    }

    [AvaloniaFact]
    public void FolderRowsOpenToTheFirstFilesAndShowAll()
    {
        var window = Open();
        var now = DateTimeOffset.Now;
        var feed = new ActivityFeed();
        for (var i = 0; i < 6; i++)
            feed.Add(new ActivityItem(now.AddSeconds(-i * 5), ActivityKind.Uploaded, $"Photos/Holiday/img-{i}.jpg", "Uploaded"));
        window.ShowActivity(feed.Items);
        window.Navigate(MainPage.Activity);
        Layout(window);
        Assert.Equal(2, window.ActivityRowCount); // today, and one folder row for the six photos
        var chevron = Screen.All<ToggleButton>(window).Single(t => t.Name == "FolderChevron");
        var peek = chevron.FindLogicalParentBorder();
        Assert.NotNull(peek);
        Assert.False(peek!.IsVisible);
        chevron.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(peek.IsVisible);
        var shown = Screen.All<TextBlock>(peek).Where(t => t.IsEffectivelyVisible && t.Text?.StartsWith("img-", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, shown.Count); // the first two files
        var all = Screen.All<ToggleButton>(peek).Single(t => t.Name == "ShowAllToggle");
        Assert.Equal("Show all 6 photos", Screen.Describe(all));
        all.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        shown = Screen.All<TextBlock>(peek).Where(t => t.IsEffectivelyVisible && t.Text?.StartsWith("img-", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(6, shown.Count);
        Close(window);
    }

    [AvaloniaFact]
    public void NeedsYouDrawsOneCardPerThingAndACalmEmptyPage()
    {
        var window = Open();
        window.Navigate(MainPage.Attention);
        Assert.True(window.AttentionEmptyShown);
        Assert.StartsWith("Nothing needs a decision", window.AttentionSubtitleValue);
        var ran = new List<string>();
        window.ShowAttention(
        [
            new AttentionItem("PC-1 wants to join", "Code RX4K-92TB.", "Review in browser", () => ran.Add("join"), AttentionKind.Join, "RX4K-92TB", DateTimeOffset.Now),
            new AttentionItem("budget.xlsx", "Both copies were kept.", "Show in folder", () => ran.Add("conflict"), AttentionKind.Conflict, null, DateTimeOffset.Now),
            new AttentionItem("Report.pdf", "Same name.", "Show in folder", () => ran.Add("clash"), AttentionKind.NameClash),
        ]);
        Layout(window);
        Assert.Equal(3, window.AttentionCards);
        Assert.False(window.AttentionEmptyShown);
        Assert.Equal("3 things only you can decide. Everything else keeps syncing meanwhile.", window.AttentionSubtitleValue);
        var texts = Screen.All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("RX4K-92TB", texts); // the code, big
        Assert.Contains("Code on PC-1", texts);
        Assert.DoesNotContain(Screen.All<Button>(window).Where(b => b.IsEffectivelyVisible), b => Screen.Describe(b) is "Deny" or "Allow"); // nothing to deny or allow in the app
        var buttons = Screen.All<Button>(window).Where(b => b.IsEffectivelyVisible && Screen.Describe(b) is "Review in browser" or "Show in folder").ToList();
        Assert.Equal(3, buttons.Count);
        Person.Click(buttons[0]);
        Assert.Equal(["join"], ran);

        window.ShowAttention([new AttentionItem("x", "y", "Show in folder", () => { })]);
        Assert.StartsWith("1 thing only you can decide", window.AttentionSubtitleValue);
        Close(window);
    }

    [AvaloniaFact]
    public void NeedsYouOffersThisComputersOwnNameWithBothChoices()
    {
        var window = Open();
        window.Navigate(MainPage.Attention);
        var ran = new List<string>();
        var offer = new NameOffer("PC-1", "StudyPC");
        window.ShowAttention([new AttentionItem(offer.Title, offer.Detail, offer.UseLabel, () => ran.Add("use"), AttentionKind.Rename,
            SecondLabel: offer.KeepLabel, Second: () => ran.Add("keep"))]);
        Layout(window);
        Assert.Equal(1, window.AttentionCards);
        var texts = Screen.All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Use this computer's own name?", texts);
        Assert.Contains(texts, t => t?.Contains("called PC-1", StringComparison.Ordinal) == true);
        var buttons = Screen.All<Button>(window).Where(b => b.IsEffectivelyVisible).ToList();
        Person.Click(buttons.Single(b => Screen.Describe(b) == "Keep PC-1"));
        Person.Click(buttons.Single(b => Screen.Describe(b) == "Use StudyPC"));
        Assert.Equal(["keep", "use"], ran);

        // Other cards have no second button.
        window.ShowAttention([new AttentionItem("x", "y", "Show in folder", () => { })]);
        Layout(window);
        Assert.DoesNotContain(Screen.All<Button>(window).Where(b => b.IsEffectivelyVisible), b => Screen.Describe(b) == "Keep PC-1");
        Close(window);
    }

    [AvaloniaFact]
    public async Task RestoreOnTheDeletedTabBringsTheNewestVersionBack()
    {
        var actions = new SampleActions();
        var window = Open(actions);
        window.Navigate(MainPage.History);
        await window.LoadHistoryAsync();
        Assert.True(window.DeletedPaneShown);
        Assert.Equal(4, window.HistoryRows);
        Assert.Equal("4", window.DeletedCountValue);
        Assert.Equal("Search deleted files by name or folder", window.HistorySearchWatermark);

        await window.RestoreDeletedRowAsync(0);
        Assert.Equal(("Projects/2026/budget-draft.xlsx", "20261006T100000000Z-c0ffee00"), Assert.Single(actions.Samples.Restored)); // the newest stored version
        Assert.Equal("Restored. budget-draft.xlsx is back on your computers in a few seconds.", window.RestoreText);

        window.ShowAllFiles();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FilesPaneShown);
        Assert.Equal("Search all files by name or folder", window.HistorySearchWatermark);
        Close(window);
    }

    [AvaloniaFact]
    public async Task ShowVersionsOnAnActivityRowOpensTheAllFilesTabOnThatFile()
    {
        var window = Open(new SampleActions());
        await window.LoadHistoryAsync();
        window.ShowVersionsFor("Notes/meeting-notes.md");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MainPage.History, window.Page);
        Assert.True(window.FilesPaneShown);
        Assert.False(window.DeletedPaneShown);
        Assert.Equal(1, window.HistoryRows);
        Assert.Equal(3, window.VersionRows);
        Close(window);
    }

    private static StatusSnapshot SixComputers(bool join)
    {
        var utc = DateTimeOffset.UtcNow;
        return StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", NestUrl = "https://nest.example.com",
            Server = new ServerInfo("id", 1, 1, "1.0.48", 412L << 30, 1L << 40),
            Devices =
            [
                new("DESKTOP", utc.AddDays(-9), utc, true, "1.0.48", "Windows"),
                new("LAPTOP", utc.AddDays(-9), utc, true, "1.0.48", "Windows"),
                new("MINI-PC", utc.AddDays(-9), utc, true, "1.0.48", "Linux"),
                new("MEDIA-PC", utc.AddDays(-9), utc.AddDays(-1), false, "1.0.47", "Linux"),
                new("OFFICE-PC", utc.AddDays(-9), utc.AddDays(-2), false, "1.0.47", "Windows"),
                new("KITCHEN-PC", utc.AddDays(-9), utc.AddDays(-3), false, "1.0.47", "Windows"),
            ],
            JoinRequests = join ? [new JoinRequest("RX4K-92TB", "PC-1", "Windows", utc)] : [],
        };
    }

    [AvaloniaFact]
    public void DevicesShowsTheNestAndOneCardPerComputer()
    {
        var actions = new SampleActions();
        var window = Open(actions);
        window.ShowStatus(SixComputers(join: true), "/home/me/Work", "DESKTOP");
        window.Navigate(MainPage.Devices);
        Layout(window);
        Assert.Equal(6, window.DeviceRows);
        Assert.Equal("6 use this nest · 3 online now", window.DevicesSummaryValue);
        Assert.True(window.JoinChipShown);
        Assert.Equal("PC-1 is asking to join", window.JoinChipValue);
        var cards = Screen.All<DeviceCardView>(window).ToList();
        Assert.Equal(6, cards.Count);
        Assert.Equal("DESKTOP", cards[0].Data!.Name); // this computer first
        Assert.True(cards[0].Data!.IsThis);
        var nest = Screen.All<TextBlock>(window).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("nest.example.com", nest);
        Assert.Contains("1.0.48", nest);
        Assert.Contains("up to date", nest);
        Assert.Contains(Format.Bytes(412L << 30), nest);

        Person.Click(Screen.Find<Button>(window, "PC-1 is asking to join Review"));
        Assert.Equal(MainPage.Attention, window.Page);

        window.ShowStatus(SixComputers(join: false), "/home/me/Work", "DESKTOP");
        Assert.False(window.JoinChipShown);
        Close(window);
    }

    [AvaloniaFact]
    public void TheComputerCardKeepsTheIconSlotAndHidesThePencilOnAnOldNest()
    {
        var window = Open(new SampleActions());
        window.ShowStatus(SixComputers(join: false), "/home/me/Work", "DESKTOP");
        window.Navigate(MainPage.Devices);
        Layout(window);
        var card = Screen.All<DeviceCardView>(window).First();
        var pencil = Screen.Named<Button>(card, "PencilButton");
        Assert.False(pencil.IsVisible);
        Assert.Equal("Change the icon for DESKTOP", Avalonia.Automation.AutomationProperties.GetName(pencil));
        Assert.Equal("More for DESKTOP", Avalonia.Automation.AutomationProperties.GetName(Screen.Named<Button>(card, "MoreButton")));
        Assert.NotNull(Screen.Named<LineIcon>(card, "Glyph").Data);
        var offline = Screen.All<DeviceCardView>(window).Single(c => c.Data!.Name == "KITCHEN-PC");
        Assert.True(Screen.Named<Rectangle>(offline, "DashRing").IsVisible); // offline: dashed outline
        Assert.False(Screen.Named<Rectangle>(card, "DashRing").IsVisible);
        Close(window);
    }

    [AvaloniaFact]
    public void TheAccountButtonOpensTheAccountPageWithNoTileChecked()
    {
        var actions = new SampleActions();
        var window = Open(actions);
        window.ShowStatus(StatusSnapshot.Initial with
        {
            NestUrl = "https://nest.example.com", Account = new AccountInfo("alex@example.com", "google", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)),
        }, "/home/me/Work", "DESKTOP");
        Layout(window);
        Person.Click(Screen.Named<Button>(window, "AccountButton"));
        Layout(window);
        Assert.Equal(MainPage.Account, window.Page);
        Assert.True(window.AccountShown);
        Assert.All(new[] { "NavOverview", "NavActivity", "NavAttention", "NavHistory", "NavDevices", "NavSettings" },
            name => Assert.False(Screen.Named<RadioButton>(window, name).IsChecked));
        var page = window.Account;
        Assert.Equal("alex@example.com", page.TitleValue);
        Assert.Equal("Signed in with Google", page.SubtitleValue);
        Assert.Equal("Google", page.MethodValue);
        Assert.Equal("29 Sep 2026", page.AddedValue);
        Assert.Equal("DESKTOP", page.DeviceValue);
        Assert.Equal("nest.example.com", page.NestValue);

        // Choosing a tile goes back to normal.
        Person.Click(Screen.Named<RadioButton>(window, "NavActivity"));
        Assert.Equal(MainPage.Activity, window.Page);
        Assert.False(window.AccountShown);
        Close(window);
    }

    [AvaloniaFact]
    public void AnOlderNestShowsTheComputerNameInsteadOfAnEmail()
    {
        var window = Open(new SampleActions());
        window.ShowStatus(StatusSnapshot.Initial with { NestUrl = "https://nest.example.com" }, "/home/me/Work", "DESKTOP");
        window.Navigate(MainPage.Account);
        Assert.Equal("DESKTOP", window.Account.TitleValue);
        Assert.Equal("On nest.example.com", window.Account.SubtitleValue);
        Assert.Equal("–", window.Account.MethodValue);
        Assert.Equal("–", window.Account.AddedValue);
        Close(window);
    }

    [AvaloniaFact]
    public void TheNotificationSwitchesAreSavedAtOnce()
    {
        var actions = new SampleActions();
        actions.Notify[NoticeKind.Update] = false;
        var window = Open(actions);
        window.Navigate(MainPage.Account);
        Layout(window);
        var attention = window.Account.NotifySwitch(NoticeKind.Attention);
        var update = window.Account.NotifySwitch(NoticeKind.Update);
        Assert.True(attention.IsChecked);
        Assert.False(update.IsChecked);
        Assert.DoesNotContain(actions.Calls, c => c.StartsWith("notify:", StringComparison.Ordinal)); // drawing the page saves nothing

        Person.Click(attention);
        Person.Click(update);
        Assert.Equal(["notify:Attention:False", "notify:Update:True"], actions.Calls.Where(c => c.StartsWith("notify:", StringComparison.Ordinal)));
        Assert.False(actions.Notifies(NoticeKind.Attention));
        Assert.True(actions.Notifies(NoticeKind.Update));

        Person.Click(Screen.Find<Button>(window, "Manage your account on sync.pairnets.app"));
        Person.Click(Screen.Find<Button>(window, "Sign out"));
        Assert.Contains("account-site", actions.Calls);
        Assert.Contains("sign-out", actions.Calls);
        // The design's two other switches do nothing in the app, so they are not drawn.
        Assert.DoesNotContain(Screen.All<ToggleSwitch>(window.Account), s => Screen.Describe(s).Contains("big sync", StringComparison.Ordinal));
        Assert.Equal(3, Screen.All<ToggleSwitch>(window.Account).Count());
        Close(window);
    }

    [AvaloniaFact]
    public void SettingsHasTheDesignsCardsAndKeepsTheStartOverCardAtTheEnd()
    {
        var view = new SettingsView(new ClientSettings
        {
            ServerUrl = "https://nest.example.com/", Folder = "/home/me/Work", DeviceName = "DESKTOP", DeviceId = "id", AccountEmail = "alex@example.com", ExtraIgnore = ["*.bak", "tmp/"],
        }, null, autoStart: true, serverVersionText: "Server 1.0.48");
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        window.Show();
        var texts = Screen.All<TextBlock>(view).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("nest.example.com", texts);
        Assert.Contains("Signed in as alex@example.com", texts);
        Assert.Contains("2 rules", texts);
        Assert.Contains("Connected", texts);
        Assert.Contains(texts, t => t is not null && t.StartsWith("Pairnets ", StringComparison.Ordinal) && t.EndsWith("your nest runs 1.0.48", StringComparison.Ordinal));
        Assert.Contains("Your files in /home/me/Work stay where they are.", texts);
        Assert.False(Screen.Named<TextBox>(view, "IgnoreBox").IsEffectivelyVisible); // closed until the card is opened
        Assert.False(Screen.Named<TextBox>(view, "ServerUrlBox").IsEffectivelyVisible);
        window.Close();
    }
}

internal static class StageTreeExtensions
{
    /// <summary>The border that holds a folder row's opened files (the chevron's grid is its sibling).</summary>
    public static Border? FindLogicalParentBorder(this ToggleButton chevron)
    {
        for (var node = (Avalonia.Visual?)chevron; node is not null; node = node.GetVisualParent())
        {
            if (node is StackPanel panel)
                return panel.Children.OfType<Border>().FirstOrDefault(b => b.BorderThickness.Left == 2);
        }
        return null;
    }
}
