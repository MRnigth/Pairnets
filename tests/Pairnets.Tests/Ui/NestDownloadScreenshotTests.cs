using Avalonia;
using Avalonia.Headless.XUnit;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// Pictures of "Downloads on the nest" (1.0.61.5) as the Windows tool draws them (tools/RenderScreens.Wpf --nest-downloads):
/// the Computers page with a running, a waiting, a finished and a stopped download, the empty card, the encrypted nest, and
/// the "Download a link on the nest" window. Written when PAIRNETS_SCREENSHOT_DIR is set.
/// </summary>
public class NestDownloadScreenshotTests
{
    /// <summary>The same four downloads as the Windows pictures.</summary>
    internal static NestDownloadList Sample(DateTimeOffset utc) => new(
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
    internal static NestDownloadList SealedSample(DateTimeOffset utc) => new(
    [
        new("d1", "releases.ubuntu.com", "ubuntu-24.04.3-desktop-amd64.iso", "Downloads", NestDownload.Running,
            2_480_000_000, 6_200_000_000, 13_000_000, utc.AddMinutes(-3)),
        new("d3", "download.blender.org", "Big Buck Bunny 4K.mp4", "Films", NestDownload.Done, 1_065_000_000, 1_065_000_000, 0, utc.AddHours(-3),
            utc.AddHours(-2), Plain: true),
    ], NestDownloadRules.AtOnce, Sealed: true);

    [AvaloniaFact]
    public void RenderNestDownloadScreens()
    {
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            Render(outDir, suffix);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static void Render(string? outDir, string suffix)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, "1.0.61.5", 412L << 30, 1L << 40);
        IReadOnlyList<DeviceInfo> devices =
        [
            new("DESKTOP", utc.AddDays(-30), utc, true, "1.0.61.5", "Linux", utc.AddMinutes(-5), "d1", "desktop"),
            new("LAPTOP", utc.AddDays(-30), utc.AddHours(-8), false, "1.0.61.5", "Windows", utc.AddHours(-8), "d2", "laptop"),
        ];
        var idle = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = devices,
            DeviceIconsSupported = true, NestDownloads = Sample(utc),
        };
        var window = new MainWindow(new SampleActions()) { Width = 1200, Height = 1000 };
        window.Show();
        window.ShowStatus(idle, "/home/me/Pairnets", "DESKTOP");
        window.Navigate(MainPage.Devices);
        Assert.Equal(4, window.DownloadRows.Count);
        DesktopUiTests.Save(window, outDir, $"desktop-nest-downloads-1-list-{suffix}.png");
        window.ShowStatus(idle with { NestDownloads = new NestDownloadList([], NestDownloadRules.AtOnce) }, "/home/me/Pairnets", "DESKTOP");
        Assert.Equal(NestDownloadsSection.EmptyNote, window.DownloadsNoteValue);
        DesktopUiTests.Save(window, outDir, $"desktop-nest-downloads-2-empty-{suffix}.png");
        window.ShowStatus(idle with { NestDownloads = SealedSample(utc) }, "/home/me/Pairnets", "DESKTOP");
        Assert.True(window.CanAddDownload);
        Assert.Equal(NestDownloadsSection.SealedNote, window.DownloadsNoteValue);
        DesktopUiTests.Save(window, outDir, $"desktop-nest-downloads-3-encrypted-{suffix}.png");
        window.AllowClose = true;
        window.Close();

        var add = new NestDownloadWindow { Link = "https://releases.ubuntu.com/24.04/ubuntu-24.04.3-desktop-amd64.iso" };
        add.Show();
        DesktopUiTests.Save(add, outDir, $"desktop-nest-downloads-4-add-{suffix}.png");
        add.Close();

        var sealedAdd = new NestDownloadWindow { Link = "https://releases.ubuntu.com/24.04/ubuntu-24.04.3-desktop-amd64.iso", Sealed = true };
        sealedAdd.Show();
        DesktopUiTests.Save(sealedAdd, outDir, $"desktop-nest-downloads-5-add-encrypted-{suffix}.png");
        sealedAdd.Close();
    }
}
