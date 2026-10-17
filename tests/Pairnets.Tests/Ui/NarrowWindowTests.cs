using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// The Mac/Linux window at the size it opens at (980 px) and at its smallest (800 px), where the 1.0.55 test found
/// texts cut off and drawn over each other: the Overview's "Nest version" card, the computer cards on Devices, the
/// speed pill on the map running under the nest, the Activity filters on two lines, and the side's "Other computers"
/// heading under "can't check now".
/// </summary>
public class NarrowWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static StatusSnapshot Synced() => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle,
        Text = "Up to date",
        LastSyncAt = Now,
        Server = new ServerInfo("id", 1, 1, "1.0.55", 948L << 30, 1L << 40),
        Devices =
        [
            new DeviceInfo("PC-2", Now.AddDays(-5), Now, true, "1.0.55", "Linux"),
            new DeviceInfo("PC-1", Now.AddDays(-5), Now, true, "1.0.55", "Windows"),
        ],
    };

    private static MainWindow Open(double width, StatusSnapshot s)
    {
        var window = new MainWindow(new SampleActions()) { Width = width, Height = 700 };
        window.Show();
        window.ShowStatus(s, "/home/me/Pairnets", "PC-2");
        Layout(window);
        return window;
    }

    private static void Layout(MainWindow window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void Close(MainWindow window)
    {
        window.AllowClose = true;
        window.Close();
    }

    /// <summary>Where <paramref name="v"/> is drawn in the window (shrinking included).</summary>
    private static Rect Where(Visual v, Visual window) => new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(window)!.Value);

    private static bool Trimmed(TextBlock t) => t.TextLayout.TextLines.Any(l => l.HasCollapsed);

    /// <summary>True when a one-line text block has less room than its whole text needs (cut off, or running over).</summary>
    private static bool Cut(TextBlock t)
    {
        var whole = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight };
        whole.Measure(Size.Infinity);
        return Trimmed(t) || whole.DesiredSize.Width > t.Bounds.Width + 0.5;
    }

    private static TextBlock Shown(Visual root, string text) =>
        Screen.All<TextBlock>(root).Single(t => t.Text == text && t.IsEffectivelyVisible && t.IsAttachedToVisualTree());

    /// <summary>Pictures of the window at 980 px (written when PAIRNETS_SCREENSHOT_DIR is set), light and dark.</summary>
    [AvaloniaFact]
    public void RenderTheNarrowWindow()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var feed = new ActivityFeed();
            for (var i = 0; i < 49; i++)
                feed.Add(new ActivityItem(DateTimeOffset.Now.AddMinutes(-i), i % 2 == 0 ? ActivityKind.Uploaded : ActivityKind.Downloaded, $"file-{i}.bin", i % 2 == 0 ? "Uploaded" : "Downloaded"));
            var window = Open(980, Synced());
            window.ShowActivity(feed.Items);
            Save(window, outDir, $"avalonia-980-overview-{suffix}.png");
            window.ShowStatus(Synced() with { Status = RunnerStatus.Syncing, Stage = SyncStage.Transferring, BytesPerSecond = 14.9 * 1024 * 1024, UpBytesPerSecond = 14.9 * 1024 * 1024, MovingUp = true }, "/home/me/Pairnets", "PC-2");
            Save(window, outDir, $"avalonia-980-syncing-{suffix}.png");
            window.ShowStatus(Synced(), "/home/me/Pairnets", "PC-2");
            window.Navigate(MainPage.Devices);
            Save(window, outDir, $"avalonia-980-devices-{suffix}.png");
            window.Navigate(MainPage.Activity);
            Save(window, outDir, $"avalonia-980-activity-{suffix}.png");
            window.Navigate(MainPage.Overview);
            window.ShowStatus(Synced() with { Status = RunnerStatus.Offline, Text = "Offline: timeout" }, "/home/me/Pairnets", "PC-2");
            Save(window, outDir, $"avalonia-980-offline-{suffix}.png");
            window.ShowStatus(StatusSnapshot.SignedOut, "/home/me/Pairnets", "PC-2");
            Save(window, outDir, $"avalonia-980-signed-out-{suffix}.png");
            Close(window);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static void Save(MainWindow window, string? outDir, string name)
    {
        Layout(window);
        Thread.Sleep(420); // let the animations settle, as the other pictures do
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        frame!.Save(Path.Combine(outDir, name));
    }

    [AvaloniaTheory]
    [InlineData(980)]
    [InlineData(800)]
    public void TheNestVersionCardShowsItsLabelAndValueInFull(double width)
    {
        var window = Open(width, Synced());
        Assert.False(Trimmed(Shown(window, "Nest version")));
        Assert.False(Trimmed(Shown(window, "1.0.55")));
        Close(window);
    }

    [AvaloniaTheory]
    [InlineData(980)]
    [InlineData(800)]
    public void TheComputerCardsOnDevicesShowTheirSystemInFull(double width)
    {
        var window = Open(width, Synced());
        window.Navigate(MainPage.Devices);
        Layout(window);
        var lines = Screen.All<TextBlock>(window).Where(t => t.Name == "SystemText" && t.IsEffectivelyVisible).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, t => Assert.False(Trimmed(t), $"'{t.Text}' is cut off"));
        foreach (var label in new[] { "Address", "Version", "Free space", "Keeps deleted files" })
            Assert.False(Cut(Shown(window, label)), $"the nest's '{label}' is cut off");
        var note = Shown(window, "To remove a computer or turn off its access, use Manage devices on your nest. Its files stay where they are.");
        Assert.True(Where(note, window).Right <= window.Bounds.Width, "the note under the cards runs out of the window");
        Close(window);
    }

    [AvaloniaTheory]
    [InlineData(980, false)]
    [InlineData(980, true)]
    [InlineData(800, true)]
    public void TheWordOrSpeedOnThisComputersLineStaysClearOfTheNest(double width, bool moving)
    {
        var s = moving
            ? Synced() with { Status = RunnerStatus.Syncing, Stage = SyncStage.Transferring, BytesPerSecond = 14.9 * 1024 * 1024, UpBytesPerSecond = 14.9 * 1024 * 1024, MovingUp = true }
            : Synced();
        var window = Open(width, s);
        var hub = Screen.All<HubView>(window).Single();
        Visual word = moving ? hub.ThisPill : hub.ThisLabel;
        Assert.True(word.IsEffectivelyVisible);
        var nest = Where(hub.NestBox, window);
        var said = Where(word, window);
        Assert.True(said.Right <= nest.Left, $"the {(moving ? "speed pill" : "word")} ends at {said.Right:0} and the nest starts at {nest.Left:0}");
        var card = Where(hub.GetVisualParent()!, window);
        Assert.True(Where(hub, window).Right <= card.Right + 0.5, "the map is wider than its card");
        Close(window);
    }

    [AvaloniaFact]
    public void TheActivityFiltersStayOnOneLineAt980()
    {
        var window = Open(980, Synced());
        var feed = new ActivityFeed();
        for (var i = 0; i < 49; i++)
            feed.Add(new ActivityItem(DateTimeOffset.Now.AddMinutes(-i), i % 2 == 0 ? ActivityKind.Uploaded : ActivityKind.Downloaded, $"f{i}.bin", "Synced"));
        window.ShowActivity(feed.Items);
        window.Navigate(MainPage.Activity);
        Layout(window);
        var pills = Screen.Named<WrapPanel>(window, "ActivityFilters").Children.Select(c => Where(c, window)).ToList();
        Assert.Equal(5, pills.Count);
        Assert.All(pills, p => Assert.Equal(pills[0].Top, p.Top, 1));
        var search = Where(Screen.Named<TextBox>(window, "ActivitySearch"), window);
        Assert.All(pills, p => Assert.False(p.Intersects(search), "a filter button is under the search box"));
        Close(window);
    }

    [AvaloniaTheory]
    [InlineData(980)]
    [InlineData(800)]
    public void OtherComputersAndItsNoteDoNotOverlap(double width)
    {
        var window = Open(width, Synced() with { Status = RunnerStatus.Offline, Text = "Offline: timeout" });
        var title = Screen.Named<TextBlock>(window, "GroupTitle");
        var note = Screen.Named<TextBlock>(window, "GroupNote");
        Assert.Equal("can’t check now", note.Text);
        Assert.False(Where(title, window).Intersects(Where(note, window)), "'Other computers' and its note are drawn over each other");

        // Fonts differ between computers (a Linux box drew both over each other): a much longer note still leaves the
        // heading whole and is cut short itself.
        note.Text = "can’t check now · last seen today at 10:45";
        Layout(window);
        var whole = new TextBlock { Text = title.Text, FontFamily = title.FontFamily, FontSize = title.FontSize, FontWeight = title.FontWeight };
        whole.Measure(Size.Infinity);
        Assert.True(whole.DesiredSize.Width <= title.Bounds.Width + 0.5,
            "'Other computers' is squeezed into less room than it needs (its text then runs over the note)");
        Assert.False(Where(title, window).Intersects(Where(note, window)), "a long note is drawn over 'Other computers'");
        Assert.True(Trimmed(note));
        Close(window);
    }
}
