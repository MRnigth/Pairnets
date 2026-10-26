using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

/// <summary>1.0.61.8: "Downloads on the nest" on the Computers page, and the "Download a link on the nest" window.</summary>
internal static class NestDownloadScreens
{
    /// <summary>A running, a waiting, a finished and a stopped download (the pictures of both apps use the same).</summary>
    public static NestDownloadList Sample(DateTimeOffset utc) => new(
    [
        new("d1", "releases.ubuntu.com", "ubuntu-24.04.3-desktop-amd64.iso", "Downloads", NestDownload.Running,
            2_480_000_000, 6_200_000_000, 13_000_000, utc.AddMinutes(-3)),
        new("d2", "cdimage.debian.org", "debian-13.1.0-amd64-netinst.iso", "Downloads", NestDownload.Queued, 0, null, 0, utc.AddMinutes(-2)),
        new("d3", "download.blender.org", "Big Buck Bunny 4K.mp4", "Films", NestDownload.Done, 1_065_000_000, 1_065_000_000, 0, utc.AddHours(-3),
            utc.AddHours(-2), "Films/Big Buck Bunny 4K.mp4"),
        new("d4", "example.com", "old-backup.zip", "Downloads", NestDownload.Failed, 0, null, 0, utc.AddDays(-1),
            utc.AddDays(-1), Error: NestDownloadErrors.NotFound),
    ], NestDownloadRules.AtOnce);

    /// <summary>An encrypted nest: one download waiting to be locked, one running.</summary>
    public static NestDownloadList SealedSample(DateTimeOffset utc) => new(
    [
        new("d1", "releases.ubuntu.com", "ubuntu-24.04.3-desktop-amd64.iso", "Downloads", NestDownload.Running,
            2_480_000_000, 6_200_000_000, 13_000_000, utc.AddMinutes(-3)),
        new("d3", "download.blender.org", "Big Buck Bunny 4K.mp4", "Films", NestDownload.Done, 1_065_000_000, 1_065_000_000, 0, utc.AddHours(-3),
            utc.AddHours(-2), Plain: true),
    ], NestDownloadRules.AtOnce, Sealed: true);

    public static void Render(string outDir, string suffix, bool print)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, "1.0.61.8", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.61.8", "Windows", utc.AddMinutes(-5), "d1", "desktop"),
            new("LAPTOP", utc.AddDays(-30), utc.AddHours(-8), false, "1.0.61.8", "Windows", utc.AddHours(-8), "d2", "laptop"),
        ];
        var idle = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = devices,
            DeviceIconsSupported = true, NestDownloads = Sample(utc),
        };
        const string folder = @"D:\Sync\Work";

        var window = new MainWindow(new Stage6.Actions()) { Width = 1200, Height = 1000 };
        Program.Place(window);
        window.ShowStatus(idle, folder, "DESKTOP");
        window.Navigate(MainPage.Devices);
        Program.Save(window, outDir, $"windows-nest-downloads-1-list-{suffix}.png", print);
        window.ShowStatus(idle with { NestDownloads = new NestDownloadList([], NestDownloadRules.AtOnce) }, folder, "DESKTOP");
        Program.Save(window, outDir, $"windows-nest-downloads-2-empty-{suffix}.png", print);
        window.ShowStatus(idle with { NestDownloads = SealedSample(utc) }, folder, "DESKTOP");
        Program.Save(window, outDir, $"windows-nest-downloads-3-encrypted-{suffix}.png", print);
        window.Close();

        var add = new NestDownloadWindow((_, _) => Task.FromResult<string?>(null), _ => null)
        {
            Link = "https://releases.ubuntu.com/24.04/ubuntu-24.04.3-desktop-amd64.iso",
        };
        Program.Place(add);
        Program.Save(add, outDir, $"windows-nest-downloads-4-add-{suffix}.png", print);
        add.Close();

        var sealedAdd = new NestDownloadWindow((_, _) => Task.FromResult<string?>(null), _ => null)
        {
            Link = "https://releases.ubuntu.com/24.04/ubuntu-24.04.3-desktop-amd64.iso", Sealed = true,
        };
        Program.Place(sealedAdd);
        Program.Save(sealedAdd, outDir, $"windows-nest-downloads-5-add-encrypted-{suffix}.png", print);
        sealedAdd.Close();
    }
}
