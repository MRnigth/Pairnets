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
        AppBuilder.Configure<Tether.Desktop.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
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

    /// <summary>
    /// Renders the windows (light and dark) to PNG files for documentation when TETHER_SCREENSHOT_DIR
    /// is set (otherwise it just checks that frames can be rendered).
    /// </summary>
    [AvaloniaFact]
    public void RenderScreenshots()
    {
        var outDir = Environment.GetEnvironmentVariable("TETHER_SCREENSHOT_DIR");
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            RenderAll(outDir, suffix);
        }
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static void RenderAll(string? outDir, string suffix)
    {
        var now = DateTimeOffset.Now;
        var window = new MainWindow { Width = 720, Height = 600 };
        window.Show();
        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(now.AddHours(-3), ActivityKind.Downloaded, "Projects/2026/plan.xlsx", "Downloaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-47), ActivityKind.Uploaded, "Notes/meeting-notes.md", "Uploaded"));
        feed.Add(new ActivityItem(now.AddMinutes(-12), ActivityKind.DeletedOnServer, "Archive/old-draft.txt", "Deleted"));
        feed.Add(new ActivityItem(now.AddMinutes(-5), ActivityKind.Conflict, "Projects/report (conflict LAPTOP 2026-10-02 141509).docx", "Conflict"));
        feed.Add(new ActivityItem(now.AddMinutes(-2), ActivityKind.Downloaded, "Photos/summer/beach.jpg", "Downloaded"));
        feed.Add(new ActivityItem(now.AddSeconds(-20), ActivityKind.Uploaded, "Photos/summer/holiday.jpg", "Uploaded"));
        window.ShowActivity(feed.Items);
        var attention = new List<AttentionItem>
        {
            new("Conflict copy: report (conflict LAPTOP 2026-10-02 141509).docx",
                "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { }),
            new("Docs/Report.TXT", "Another file with the same name in different letter case exists on the server.", "Show in folder", () => { }),
        };
        window.ShowAttention(attention);

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1),
            CurrentPath = "Videos/presentation-final.mp4", Operation = "upload", BytesDone = 67_108_864, BytesTotal = 104_857_600, FilesDone = 2, FilesTotal = 5,
        }, "/Users/me/Work");
        Save(window, outDir, $"main-window-syncing-{suffix}.png");

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, LastSyncAt = now.AddMinutes(-3),
            Text = "This sync would delete 37 files on the server (out of 120). Nothing was deleted.",
        }, "/Users/me/Work");
        window.ShowAttentionTab(true);
        Save(window, outDir, $"main-window-blocked-{suffix}.png");

        window.ShowAttentionTab(false);
        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now }, "/Users/me/Work");
        Save(window, outDir, $"main-window-idle-{suffix}.png");
        window.AllowClose = true;
        window.Close();

        var settings = new SettingsWindow(new ClientSettings { DeviceName = "MacBook" }, null, firstRun: true, autoStart: true);
        settings.Show();
        settings.ShowTestResult(true, "Connected. Server and token are OK.");
        Save(settings, outDir, $"settings-first-run-{suffix}.png");
        settings.Close();
    }

    private static void Save(Avalonia.Controls.Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        frame!.Save(Path.Combine(outDir, name));
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
