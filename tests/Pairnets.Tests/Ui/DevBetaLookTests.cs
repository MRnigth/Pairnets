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
/// The Developer beta look in the Avalonia app (top bar, bell, progress ring). The look is chosen once per process
/// (<see cref="DevBetaLook.On"/>, from PAIRNETS_LOOK=devbeta or the build's channel), so these checks do their work only in
/// a run with that variable set; a normal run passes them without checking anything, and the stable look is covered by
/// the existing tests. Pictures go to PAIRNETS_SCREENSHOT_DIR when it is set. Windows open headless.
/// </summary>
[Collection(AvaloniaUi.Name)]
public class DevBetaLookTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static StatusSnapshot Synced() => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = Now,
        Server = new ServerInfo("id", 1, 1, "1.0.61", 948L << 30, 1L << 40),
        Devices = [new DeviceInfo("PC-2", Now.AddDays(-5), Now, true, "1.0.61", "Linux"), new DeviceInfo("PC-1", Now.AddDays(-5), Now, true, "1.0.61", "Windows")],
    };

    private static StatusSnapshot Syncing() => Synced() with
    {
        Status = RunnerStatus.Syncing, Text = "Syncing", FilesDone = 312, FilesTotal = 1204, CurrentPath = "Photos/a.jpg", Operation = "upload",
        BytesPerSecond = 4.9 * (1 << 20), UpBytesPerSecond = 4.9 * (1 << 20),
        Active = [new ActiveTransfer("Photos/a.jpg", "upload", 40, 100), new ActiveTransfer("Photos/b.jpg", "upload", 10, 100)],
    };

    private static void Layout(MainWindow window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static MainWindow Open(double width, StatusSnapshot s, int attention = 3, bool update = true, bool nests = false)
    {
        var window = new MainWindow(new SampleActions()) { Width = width, Height = 720 };
        window.Show();
        window.ShowStatus(s, "/home/me/Pairnets", "PC-2");
        if (update)
            window.ShowUpdate("Pairnets 1.0.62 is available", "You have 1.0.61.", "Update now");
        window.ShowAttention(Enumerable.Range(0, attention).Select(i => new AttentionItem($"Conflict copy: report-{i}.docx", "Both computers changed this file.", "Show in folder", () => { })).ToList());
        if (nests)
        {
            NestListRow Row(string id, string title, bool selected) => new(id, title, "/home/me/" + title, "up to date", title + " · up to date", SideDot.Ok, SideTone.Neutral, RunnerStatus.Idle, null, selected, "PC-2", null);
            window.ShowNests(new NestListModel([Row("a", "Home nest", false), Row("b", "Work nest with a long name", true)], "2 nests", false, RunnerStatus.Idle));
        }
        Layout(window);
        return window;
    }

    private static void Close(MainWindow window)
    {
        window.AllowClose = true;
        window.Close();
    }

    private static void Save(MainWindow window, string? outDir, string name)
    {
        Layout(window);
        Thread.Sleep(700); // the ring's tween is 570 ms
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        frame!.Save(Path.Combine(outDir, name));
    }

    private static Rect Where(Visual v, Visual window) => new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(window)!.Value);

    [AvaloniaTheory]
    [InlineData(1200)]
    [InlineData(800)]
    public void TheTopBarFitsTheWindowAndReplacesTheSidePanel(double width)
    {
        if (!DevBetaLook.On)
            return;
        var window = Open(width, Synced(), nests: true);
        Assert.True(window.HasTopBar);
        Assert.False(window.FindControl<Grid>("SideOpen")!.IsVisible);
        Assert.False(window.FindControl<Grid>("SideDock")!.IsVisible);
        var bar = window.GetVisualDescendants().OfType<Border>().First(b => b.CornerRadius.TopLeft >= 26 && b.IsEffectivelyVisible);
        var barRect = Where(bar, window);
        Assert.True(barRect.Left >= 0 && barRect.Right <= window.Width + 0.5, $"bar {barRect} in {window.Width}");
        foreach (var pill in Screen.All<Button>(bar).Where(b => b.IsEffectivelyVisible))
        {
            var r = Where(pill, window);
            Assert.True(r.Left >= barRect.Left - 0.5 && r.Right <= barRect.Right + 0.5, $"{pill.Content} {r} outside {barRect}");
            Assert.True(r.Bottom <= barRect.Bottom + 0.5, $"{pill.Content} {r} below the bar {barRect}");
        }
        Close(window);
    }

    [AvaloniaFact]
    public void ThePillsGoToTheirPagesAndTheBellListsWhatNeedsYou()
    {
        if (!DevBetaLook.On)
            return;
        var window = Open(1000, Synced());
        Button Pill(string text) => Screen.All<Button>(window).Single(b => b.IsEffectivelyVisible && b.Content as string == text);
        Pill("Activity").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Layout(window);
        Assert.Equal(MainPage.Activity, window.Page);
        Pill("Computers").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Layout(window);
        Assert.Equal(MainPage.Devices, window.Page);
        Pill("Overview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Layout(window);
        Assert.Equal(MainPage.Overview, window.Page);
        Assert.Contains(Screen.All<TextBlock>(window), t => t.Text == "3" && t.IsEffectivelyVisible);
        Close(window);
    }

    [AvaloniaFact]
    public void TheRingFollowsTheStateAndStopsMovingWhenTheSyncEnds()
    {
        if (!DevBetaLook.On)
            return;
        var window = Open(1000, Syncing());
        Layout(window);
        var ring = Screen.All<ProgressRing>(window).Single();
        Assert.Equal("312 of 1,204", Avalonia.Automation.AutomationProperties.GetName(ring));
        Assert.True(ring.SpinnerPresent || ring.FramesRequested);
        window.ShowStatus(Synced(), "/home/me/Pairnets", "PC-2");
        for (var i = 0; i < 40 && (ring.SpinnerPresent || ring.FramesRequested); i++)
        {
            Thread.Sleep(50);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(ring.SpinnerPresent);
        Assert.False(ring.FramesRequested); // idle: no frames asked for
        Close(window);
    }

    [AvaloniaFact]
    public void RenderTheDevBetaLook()
    {
        if (!DevBetaLook.On)
            return;
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var window = Open(1100, Syncing(), nests: true);
            Save(window, outDir, $"devbeta-syncing-{suffix}.png");
            window.ShowStatus(Synced(), "/home/me/Pairnets", "PC-2");
            Save(window, outDir, $"devbeta-uptodate-{suffix}.png");
            Close(window);

            var narrow = Open(800, Syncing(), nests: true);
            Save(narrow, outDir, $"devbeta-800-{suffix}.png");
            narrow.Navigate(MainPage.Devices);
            Save(narrow, outDir, $"devbeta-800-devices-{suffix}.png");
            Close(narrow);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }
}
