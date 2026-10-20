using System.Windows;
using System.Windows.Controls;
using Pairnets.Client.Themes;
using Pairnets.Client.Tests;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

/// <summary>Stage 6: the icon of each computer (chooser, Devices page, left side, Hub, dock).</summary>
internal static class Stage6
{
    internal sealed class Actions : IMainActions
    {
        public void FixBlocked() { }
        public void SyncNow() { }
        public void TogglePause() { }
        public void OpenFolder() { }
        public void ShowSettings() { }
        public void ViewLog() { }
        public void UpdateNow() { }
        public void DismissUpdate() { }
        public void DownloadNow() { }
        public void UpdateServer() { }
        public void ReportBug() { }
        public void RefreshDevices() { }
        public Task<bool> SetDeviceIcon(string id, string icon) => Task.FromResult(true);
        public void AddComputer() { }
        public void ManageDevices() { }
        public void RevealFile(string syncPath) { }
        public IHistorySource? History => null;
    }

    public static void Render(string outDir, string suffix, bool print)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, "1.0.58", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows", utc.AddMinutes(-5), "d1", "desktop"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.58", "Windows", utc.AddMinutes(-9), "d2", "laptop"),
            new("MINI-PC", utc.AddDays(-30), utc.AddSeconds(-3), true, "1.0.58", "Linux", utc.AddMinutes(-20), "d3", "mini"),
            new("MEDIA-PC", utc.AddDays(-30), utc.AddDays(-2), false, "1.0.58", "Linux", utc.AddDays(-2), "d4", "tv"),
            new("OFFICE-PC", utc.AddDays(-30), utc.AddDays(-3), false, "1.0.58", "Windows", utc.AddDays(-3), "d5", "office"),
            new("KITCHEN-PC", utc.AddDays(-30), utc.AddDays(-4), false, "1.0.58", "Windows", utc.AddDays(-4), "d6", null),
        ];
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = devices, DeviceIconsSupported = true };
        const string folder = @"D:\Sync\Work";

        var window = new MainWindow(new Actions()) { Width = 1200, Height = 860 };
        Program.Place(window);
        window.ShowStatus(idle, folder, "DESKTOP");
        Program.Save(window, outDir, $"windows-stage6-overview-{suffix}.png", print);
        window.Navigate(MainPage.Devices);
        Program.Save(window, outDir, $"windows-stage6-devices-{suffix}.png", print);
        window.SideFolded = true;
        window.Navigate(MainPage.Overview);
        Program.Save(window, outDir, $"windows-stage6-dock-{suffix}.png", print);
        window.SideFolded = false;
        window.ShowStatus(idle with { DeviceIconsSupported = false }, folder, "DESKTOP");
        window.Navigate(MainPage.Devices);
        Program.Save(window, outDir, $"windows-stage6-devices-old-nest-{suffix}.png", print);
        window.Close();

        var chooser = new IconChooser { ComputerName = "OFFICE-PC", Selected = DeviceIconKind.Office };
        var host = new Window { Width = 460, Height = 340, Content = new Border { Padding = new Thickness(30), Child = chooser } };
        chooser.HorizontalAlignment = HorizontalAlignment.Left;
        chooser.VerticalAlignment = VerticalAlignment.Top;
        ThemeManager.Attach(host);
        Program.Place(host);
        Program.Save(host, outDir, $"windows-stage6-chooser-{suffix}.png", print);
        host.Close();
    }
}
