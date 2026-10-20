using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Tests;

/// <summary>
/// The Windows app at its smallest width (940 px), with the same checks as the Mac/Linux app at 980 px: the Overview's
/// "Nest version" card, the computer cards on Devices, the speed pill on the map clear of the nest, the Activity
/// filters on one line, and the side's "Other computers" heading clear of its note.
/// </summary>
public sealed class NarrowWindowTests
{
    private const double Width = 940;

    private static WpfThread Wpf => WpfThread.Instance;

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static StatusSnapshot Synced() => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle,
        Text = "Up to date",
        LastSyncAt = Now,
        Server = new ServerInfo("id", 1, 1, "1.0.55", 948L << 30, 1L << 40),
        Devices =
        [
            new DeviceInfo("PC-1", Now.AddDays(-5), Now, true, "1.0.55", "Windows"),
            new DeviceInfo("PC-2", Now.AddDays(-5), Now, true, "1.0.55", "Linux"),
        ],
    };

    private static async Task<MainWindow> OpenAsync(StatusSnapshot s)
    {
        var window = await Wpf.Ui(() =>
        {
            var w = new MainWindow(new RecordingActions(new SampleHistory())) { Width = Width, Height = 760 };
            w.Show();
            w.ShowStatus(s, @"D:\Sync\Work", "PC-1");
            return w;
        });
        await Wpf.WaitUntil(() => window.IsLoaded, "the window to open");
        await Wpf.Settle();
        return window;
    }

    private static async Task Done()
    {
        await Wpf.Settle();
        Assert.Empty(Wpf.TakeCrashes());
        await Wpf.CloseAllWindows();
    }

    /// <summary>Where <paramref name="e"/> is drawn in the window (shrinking included).</summary>
    private static Rect Where(FrameworkElement e, Visual window) =>
        e.TransformToVisual(window).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    /// <summary>True when the text block has less room than its whole text needs (it is cut off, or runs over).</summary>
    private static bool Cut(TextBlock t)
    {
        var whole = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight };
        whole.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return whole.DesiredSize.Width > t.ActualWidth + 0.5;
    }

    private static List<TextBlock> Shown(Window window) => Presser.Elements(window).OfType<TextBlock>().Where(t => t.IsVisible).ToList();

    [Fact]
    public async Task TheNestVersionCardShowsItsLabelAndValueInFull()
    {
        var window = await OpenAsync(Synced());
        await Wpf.Ui(() =>
        {
            var texts = Shown(window);
            Assert.False(Cut(texts.Single(t => t.Text == "Nest version")));
            Assert.False(Cut(texts.Single(t => t.Text == "1.0.55")));
        });
        await Done();
    }

    [Fact]
    public async Task TheComputerCardsOnDevicesShowTheirSystemInFull()
    {
        var window = await OpenAsync(Synced());
        await Wpf.Ui(() => window.Navigate(MainPage.Devices));
        await Wpf.Settle();
        await Wpf.Ui(() =>
        {
            var lines = Shown(window).Where(t => t.Name == "SystemText").ToList();
            Assert.Equal(2, lines.Count);
            Assert.All(lines, t => Assert.False(Cut(t), $"'{t.Text}' is cut off"));
            var texts = Shown(window);
            foreach (var label in new[] { "Address", "Version", "Free space", "Keeps deleted files" })
                Assert.False(Cut(texts.Single(t => t.Text == label)), $"the nest's '{label}' is cut off");
            var note = texts.Single(t => t.Text.StartsWith("To remove a computer", StringComparison.Ordinal));
            Assert.True(Where(note, window).Right <= window.ActualWidth, "the note under the cards runs out of the window");
        });
        await Done();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheWordOrSpeedOnThisComputersLineStaysClearOfTheNest(bool moving)
    {
        var s = moving
            ? Synced() with { Status = RunnerStatus.Syncing, Stage = SyncStage.Transferring, BytesPerSecond = 14.9 * 1024 * 1024, UpBytesPerSecond = 14.9 * 1024 * 1024, MovingUp = true }
            : Synced();
        var window = await OpenAsync(s);
        await Wpf.Ui(() =>
        {
            var hub = Presser.Elements(window).OfType<HubView>().Single();
            FrameworkElement word = moving ? hub.ThisPill : hub.ThisLabel;
            Assert.True(word.IsVisible);
            var nest = Where(hub.NestBox, window);
            var said = Where(word, window);
            Assert.True(said.Right <= nest.Left, $"the {(moving ? "speed pill" : "word")} ends at {said.Right:0} and the nest starts at {nest.Left:0}");
        });
        await Done();
    }

    [Fact]
    public async Task TheActivityFiltersStayOnOneLine()
    {
        var window = await OpenAsync(Synced());
        var feed = new ActivityFeed();
        for (var i = 0; i < 49; i++)
            feed.Add(new ActivityItem(DateTimeOffset.Now.AddMinutes(-i), i % 2 == 0 ? ActivityKind.Uploaded : ActivityKind.Downloaded, $"f{i}.bin", "Synced"));
        await Wpf.Ui(() =>
        {
            window.ShowActivity(feed.Items);
            window.Navigate(MainPage.Activity);
        });
        await Wpf.Settle();
        await Wpf.Ui(() =>
        {
            var page = (ActivityPageView)window.FindName("ActivityPage");
            var pills = ((Panel)page.FindName("Filters")).Children.OfType<FrameworkElement>().Select(p => Where(p, window)).ToList();
            Assert.Equal(5, pills.Count);
            Assert.All(pills, p => Assert.Equal(pills[0].Top, p.Top, 1));
            var search = Where((FrameworkElement)page.FindName("Search"), window);
            Assert.All(pills, p => Assert.False(p.IntersectsWith(search), "a filter button is under the search box"));
        });
        await Done();
    }

    [Fact]
    public async Task TheFilesPageKeepsItsToolsApartAndItsNamesReadable()
    {
        var window = await OpenAsync(Synced());
        await Wpf.Ui(() => window.Navigate(MainPage.Files));
        await Wpf.Settle();
        await Wpf.Ui(() =>
        {
            var page = window.FilesPane;
            var search = Where(page.FilesSearch, window);
            var add = Where(page.AddFilesButton, window);
            Assert.False(search.IntersectsWith(add), "Add files is drawn over the search box");
            Assert.True(add.Right <= Where(page, window).Right + 0.5, "Add files is cut off at the right");
            var card = Where(page.ListCard, window);
            foreach (var more in Presser.Elements(page.EntryList).OfType<Button>().Where(b => b.ToolTip as string == "More" && b.IsVisible))
                Assert.True(Where(more, window).Right <= card.Right + 0.5, "a row's … button is cut off");
            var name = Presser.Elements(page.EntryList).OfType<TextBlock>().First(t => t.Text == "Documents");
            var column = ((FrameworkElement)name.Parent).ActualWidth;
            Assert.True(column >= 100, $"the names get only {column:0} px");
        });
        await Done();
    }

    [Fact]
    public async Task OtherComputersAndItsNoteDoNotOverlap()
    {
        var window = await OpenAsync(Synced() with { Status = RunnerStatus.Offline, Text = "Offline: timeout" });
        var title = await Wpf.Ui(() => (TextBlock)window.FindName("GroupTitle"));
        var note = await Wpf.Ui(() => (TextBlock)window.FindName("GroupNote"));
        Assert.Equal("can’t check now", await Wpf.Ui(() => note.Text));

        // A much longer note (fonts differ between computers) still leaves the heading whole.
        await Wpf.Ui(() => note.Text = "can’t check now · last seen today at 10:45");
        await Wpf.Settle();
        await Wpf.Ui(() =>
        {
            Assert.False(Cut(title), "'Other computers' is squeezed into less room than it needs");
            Assert.False(Where(title, window).IntersectsWith(Where(note, window)), "the note is drawn over 'Other computers'");
        });
        await Done();
    }
}
