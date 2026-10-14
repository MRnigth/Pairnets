using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>Stage 6 on the macOS/Linux app: the icon chooser, and a computer's chosen icon wherever it is drawn.</summary>
public class DeviceIconUiTests
{
    private static readonly DateTimeOffset Utc = DateTimeOffset.UtcNow;

    private static IReadOnlyList<DeviceInfo> Devices() =>
    [
        new("MacBook", Utc.AddDays(-30), Utc, true, "1.0.58", "macOS", Utc.AddMinutes(-5), "d1", "mini"),
        new("DESKTOP", Utc.AddDays(-30), Utc, true, "1.0.58", "Windows", Utc.AddMinutes(-9), "d2", "office"),
        new("LAPTOP", Utc.AddDays(-30), Utc, true, "1.0.58", "Windows", Utc.AddMinutes(-9), "d3", "laptop"),
        new("MINI-PC", Utc.AddDays(-30), Utc.AddSeconds(-3), true, "1.0.58", "Linux", Utc.AddMinutes(-20), "d4", "mini"),
        new("MEDIA-PC", Utc.AddDays(-30), Utc.AddDays(-2), false, "1.0.58", "Linux", Utc.AddDays(-2), "d5", "tv"),
        new("OFFICE-PC", Utc.AddDays(-30), Utc.AddDays(-3), false, "1.0.58", "Windows", Utc.AddDays(-3), "d6", "office"),
    ];

    private static StatusSnapshot Idle(bool supportsIcons) => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = Utc.AddMinutes(-1), DeviceIconsSupported = supportsIcons,
        Server = new ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40), Devices = Devices(),
    };

    private static List<DeviceIconPencil> Pencils(MainWindow window) =>
        window.GetVisualDescendants().OfType<DeviceIconPencil>().Where(p => p.IsEffectivelyVisible).ToList();

    [AvaloniaFact]
    public void ThePencilsShowOnlyWhenTheNestKnowsIconsAndAPickIsSavedAtOnce()
    {
        var actions = new SampleActions();
        var window = new MainWindow(actions) { ThisDevice = "MacBook", Width = 1200, Height = 860 };
        window.Show();
        window.ShowStatus(Idle(supportsIcons: false), null);
        window.Navigate(MainPage.Devices);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Empty(Pencils(window)); // an old nest: no pencils, every computer drawn as a desktop or laptop by its name

        window.ShowStatus(Idle(supportsIcons: true), null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var pencils = Pencils(window);
        Assert.Equal(6, pencils.Count);
        var laptop = pencils.Single(p => p.DeviceName == "LAPTOP");
        Assert.Equal("d3", laptop.DeviceId);
        Assert.Equal(DeviceIconKind.Laptop, laptop.Current);

        laptop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(laptop.PopoverFlyout.IsOpen);
        Assert.Equal("Icon for LAPTOP", laptop.Chooser.ComputerName);
        Assert.True(laptop.Chooser.IsMarked(DeviceIconKind.Laptop));
        Assert.False(laptop.Chooser.IsMarked(DeviceIconKind.Tv));

        laptop.Chooser.ButtonFor(DeviceIconKind.Tv).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("icon:d3:tv", actions.Calls); // saved through the controller as soon as it is picked
        Assert.False(laptop.PopoverFlyout.IsOpen);

        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void TheChosenIconIsDrawnOnTheLeftSideTheDockAndTheDevicesRows()
    {
        var window = new MainWindow(new SampleActions()) { ThisDevice = "MacBook", Width = 1200, Height = 860 };
        window.Show();
        window.ShowStatus(Idle(supportsIcons: true), null);
        window.Navigate(MainPage.Devices);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(DeviceIconArt.For(DeviceIconKind.Mini), window.FindControl<LineIcon>("ThisIcon")!.Data);
        Assert.Equal(DeviceIconArt.For(DeviceIconKind.Mini), window.FindControl<LineIcon>("DockThisIcon")!.Data);
        var dock = new[] { "DockOther1Icon", "DockOther2Icon" }.Select(n => window.FindControl<LineIcon>(n)!.Data).ToList();
        Assert.Contains(DeviceIconArt.For(DeviceIconKind.Office), dock);
        Assert.Contains(DeviceIconArt.For(DeviceIconKind.Laptop), dock);
        var rowIcons = window.GetVisualDescendants().OfType<LineIcon>().Select(i => i.Data).ToList();
        Assert.Contains(DeviceIconArt.For(DeviceIconKind.Tv), rowIcons);
        Assert.Contains(DeviceIconArt.For(DeviceIconKind.Office), rowIcons);

        // An old nest cannot save icons, so nothing shows as chosen: desktop (or a laptop by its name).
        window.ShowStatus(Idle(supportsIcons: false), null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(DeviceIconArt.For(DeviceIconKind.Laptop), window.FindControl<LineIcon>("ThisIcon")!.Data); // "MacBook": the guess from the name
        dock = new[] { "DockOther1Icon", "DockOther2Icon" }.Select(n => window.FindControl<LineIcon>(n)!.Data).ToList();
        Assert.Contains(DeviceIconArt.For(DeviceIconKind.Desktop), dock); // DESKTOP: the office icon is not known to the old nest
        Assert.DoesNotContain(DeviceIconArt.For(DeviceIconKind.Office), dock);

        window.AllowClose = true;
        window.Close();
    }

    /// <summary>Renders the stage 6 screens when PAIRNETS_SCREENSHOT_DIR is set (otherwise it only checks that frames render).</summary>
    [AvaloniaFact]
    public void RenderStage6Screenshots()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var window = new MainWindow(new SampleActions()) { ThisDevice = "MacBook", Width = 1200, Height = 860 };
            window.Show();
            window.ShowStatus(Idle(supportsIcons: true), "/home/me/Work");
            Save(window, outDir, $"stage6-overview-{suffix}.png");
            window.Navigate(MainPage.Devices);
            Save(window, outDir, $"stage6-devices-{suffix}.png");
            window.SideFolded = true;
            window.Navigate(MainPage.Overview);
            Save(window, outDir, $"stage6-dock-{suffix}.png");
            window.SideFolded = false;
            window.ShowStatus(Idle(supportsIcons: false), "/home/me/Work");
            window.Navigate(MainPage.Devices);
            Save(window, outDir, $"stage6-devices-old-nest-{suffix}.png");
            window.AllowClose = true;
            window.Close();

            var chooser = new IconChooser { ComputerName = "OFFICE-PC", Selected = DeviceIconKind.Office, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var host = new Window { Width = 420, Height = 240, Content = new Border { Padding = new Thickness(30), Child = chooser } };
            host.Show();
            Save(host, outDir, $"stage6-chooser-{suffix}.png");
            host.Close();
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static void Save(Window window, string? outDir, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Thread.Sleep(420);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        frame!.Save(Path.Combine(outDir, name));
    }
}
