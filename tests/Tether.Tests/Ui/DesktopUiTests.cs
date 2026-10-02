using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Tether.Core.Client;
using Tether.Core.Settings;
using Tether.Core.Sync;
using Tether.Desktop.Platform;
using Tether.Desktop.Views;
using Tether.Tests.Infrastructure;

[assembly: AvaloniaTestApplication(typeof(Tether.Tests.Ui.HeadlessApp))]

namespace Tether.Tests.Ui;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Tether.Desktop.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Loads the macOS/Linux windows headlessly: XAML parses, controls bind, states render.</summary>
public class DesktopUiTests
{
    [AvaloniaFact]
    public void MainWindowShowsStatusActivityAndFixAction()
    {
        var window = new MainWindow();
        window.Show();
        Assert.Equal(0, window.ActivityCount);

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, CurrentPath = "a/big.bin", Operation = "upload", BytesDone = 5, BytesTotal = 10, FilesTotal = 3 }, "/home/me/Work");
        Assert.Equal("Syncing…", window.HeadlineText);
        Assert.True(window.TransferVisible);
        Assert.False(window.FixVisible);

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 12 }, null);
        Assert.Equal("Needs your decision", window.HeadlineText);
        Assert.True(window.FixVisible);
        Assert.False(window.TransferVisible);

        var feed = new ActivityFeed();
        feed.Add(ActivityKind.Uploaded, "a.txt", "Uploaded: a.txt");
        feed.Add(ActivityKind.Conflict, "b (conflict MAC 2026-10-02 101010).txt", "Conflict on b.txt");
        window.ShowActivity(feed.Items);
        Assert.Equal(2, window.ActivityCount);
        window.ShowAttention([new AttentionItem("x", "y", "Show in folder", () => { })]);
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsWindowLoadsInFirstRunAndSettingsModes()
    {
        var first = new SettingsWindow(new ClientSettings(), null, firstRun: true, autoStart: false);
        first.Show();
        Assert.Contains("first-time", first.Title);
        first.Close();
        var later = new SettingsWindow(new ClientSettings { ServerUrl = "http://example.invalid:5075/", Folder = "/tmp/x", DeviceName = "mac" }, null, firstRun: false, autoStart: true);
        later.Show();
        Assert.Contains("settings", later.Title);
        later.Close();
    }

    [Fact]
    public void PrivateFileSecretsAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return; // the file fallback exists only for Linux/macOS; Windows uses DPAPI
        using var dir = new TempDir("secret");
        var path = Path.Combine(dir.Path, "sub", "token");
        var store = new PrivateFileSecrets(path);
        var marker = store.Protect("a-very-secret-token-value");
        Assert.Equal("file:v1", marker);
        Assert.Equal("a-very-secret-token-value", store.Unprotect(marker));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
