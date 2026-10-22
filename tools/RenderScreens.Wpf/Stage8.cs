using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

/// <summary>Stage 8: the left side shows where you are (this computer on the Overview, the nest on Computers).</summary>
internal static class Stage8
{
    public static void Render(string outDir, string suffix, bool print)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, "1.0.61", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.61", "Windows", utc.AddMinutes(-5), "d1", "desktop"),
            new("LAPTOP", utc.AddDays(-30), utc, true, "1.0.61", "Windows", utc.AddMinutes(-9), "d2", "laptop"),
            new("MEDIA-PC", utc.AddDays(-30), utc.AddDays(-2), false, "1.0.61", "Linux", utc.AddDays(-2), "d4", "tv"),
        ];
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = devices, DeviceIconsSupported = true };
        var stopped = idle with { Status = RunnerStatus.Blocked, Text = "The synced folder is missing", BlockReason = BlockReason.FolderMissing };
        const string folder = @"D:\Sync\Work";

        var window = new MainWindow(new Stage6.Actions()) { Width = 1200, Height = 860 };
        Program.Place(window);
        window.ShowStatus(idle, folder, "DESKTOP");
        window.Navigate(MainPage.Overview);
        Program.Save(window, outDir, $"windows-sidebar-1-overview-{suffix}.png", print);
        window.Navigate(MainPage.Devices);
        Program.Save(window, outDir, $"windows-sidebar-2-computers-{suffix}.png", print);
        window.ShowStatus(stopped, folder, "DESKTOP");
        window.Navigate(MainPage.Overview);
        Program.Save(window, outDir, $"windows-sidebar-3-warn-overview-{suffix}.png", print);
        window.ShowStatus(idle, folder, "DESKTOP");
        window.SideFolded = true;
        window.Navigate(MainPage.Devices);
        Program.Save(window, outDir, $"windows-sidebar-4-dock-computers-{suffix}.png", print);
        window.Close();
    }
}
