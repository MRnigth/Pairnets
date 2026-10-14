using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Tests;

/// <summary>Stage 6 on Windows: the icon chooser, and a computer's chosen icon wherever it is drawn.</summary>
public sealed class DeviceIconTests
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static IReadOnlyList<DeviceInfo> Devices()
    {
        var utc = DateTimeOffset.UtcNow;
        return
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows", utc.AddMinutes(-5), "d1", "mini"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows", utc.AddMinutes(-9), "d2", "laptop"),
            new("MEDIA-PC", utc.AddDays(-30), utc.AddDays(-2), false, "1.0.58", "Linux", utc.AddDays(-2), "d3", "tv"),
            new("OFFICE-PC", utc.AddDays(-30), utc.AddDays(-3), false, "1.0.58", "Windows", utc.AddDays(-3), "d4", "office"),
        ];
    }

    private static StatusSnapshot Idle(bool supportsIcons) => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = DateTimeOffset.UtcNow.AddMinutes(-1), DeviceIconsSupported = supportsIcons,
        Server = new ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40), Devices = Devices(),
    };

    private static List<DeviceIconPencil> Pencils(MainWindow window) =>
        Presser.Elements(window).OfType<DeviceIconPencil>().Where(p => p.IsVisible).ToList();

    [Fact]
    public async Task ThePencilsShowOnlyWhenTheNestKnowsIconsAndAPickIsSavedAtOnce()
    {
        var actions = new RecordingActions();
        await Wpf.Ui(() =>
        {
            var window = new MainWindow(actions) { ThisDevice = "DESKTOP", Width = 1200, Height = 860 };
            window.Left = -20000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            try
            {
                window.ShowStatus(Idle(supportsIcons: false), @"D:\Sync\Work", "DESKTOP");
                window.Navigate(MainPage.Devices);
                window.UpdateLayout();
                Assert.Empty(Pencils(window)); // an old nest: no pencils

                window.ShowStatus(Idle(supportsIcons: true), @"D:\Sync\Work", "DESKTOP");
                window.UpdateLayout();
                var pencils = Pencils(window);
                Assert.Equal(4, pencils.Count);
                var laptop = pencils.Single(p => p.DeviceName == "LAPTOP");
                Assert.Equal("d2", laptop.DeviceId);
                Assert.Equal(DeviceIconKind.Laptop, laptop.Current);

                laptop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(laptop.Popup.IsOpen);
                Assert.Equal("Icon for LAPTOP", laptop.Chooser.ComputerName);
                Assert.True(laptop.Chooser.IsMarked(DeviceIconKind.Laptop));
                Assert.False(laptop.Chooser.IsMarked(DeviceIconKind.Tv));

                laptop.Chooser.ButtonFor(DeviceIconKind.Tv).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(laptop.Popup.IsOpen);
            }
            finally
            {
                window.AllowClose = true;
                window.Close();
            }
        });
        await WaitFor(() => actions.Calls.Items.Contains("icon:d2:tv")); // saved through the controller as soon as it is picked
    }

    [Fact]
    public async Task TheChosenIconIsDrawnOnTheLeftSideTheDockAndTheMap()
    {
        await Wpf.Ui(() =>
        {
            var window = new MainWindow(new RecordingActions()) { ThisDevice = "DESKTOP", Width = 1200, Height = 860 };
            window.Left = -20000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            try
            {
                window.ShowStatus(Idle(supportsIcons: true), @"D:\Sync\Work", "DESKTOP");
                window.UpdateLayout();
                var mini = DeviceIconArt.For(DeviceIconKind.Mini);
                Assert.Equal(mini, window.ThisIcon.Data);
                Assert.Equal(mini, window.DockThisIcon.Data);
                Assert.Equal(DeviceIconArt.For(DeviceIconKind.Laptop), window.DockOther1Icon.Data);
                Assert.Equal(mini, window.Overview.ThisIconData);
                Assert.Equal(DeviceIconArt.For(DeviceIconKind.Laptop), window.Overview.OtherIconData);

                window.ShowStatus(Idle(supportsIcons: false), @"D:\Sync\Work", "DESKTOP"); // an old nest: nothing looks chosen
                window.UpdateLayout();
                Assert.Equal(DeviceIconArt.For(DeviceIconKind.Desktop), window.ThisIcon.Data);
                Assert.Equal(DeviceIconArt.For(DeviceIconKind.Desktop), window.Overview.ThisIconData);
            }
            finally
            {
                window.AllowClose = true;
                window.Close();
            }
        });
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The icon was never saved.");
            await Task.Delay(25);
        }
    }
}
