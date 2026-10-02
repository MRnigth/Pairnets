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
    /// Renders the windows to PNG files for documentation when TETHER_SCREENSHOT_DIR is set
    /// (otherwise it just checks that a frame can be rendered).
    /// </summary>
    [AvaloniaFact]
    public void RenderScreenshots()
    {
        var outDir = Environment.GetEnvironmentVariable("TETHER_SCREENSHOT_DIR");
        var now = DateTimeOffset.Now;

        var window = new MainWindow { Width = 680, Height = 540 };
        window.Show();
        var feed = new ActivityFeed();
        feed.Add(new ActivityItem(now.AddMinutes(-9), ActivityKind.Downloaded, "Projects/plan.xlsx", "Downloaded: Projects/plan.xlsx"));
        feed.Add(new ActivityItem(now.AddMinutes(-7), ActivityKind.Uploaded, "Notes/meeting.md", "Uploaded: Notes/meeting.md"));
        feed.Add(new ActivityItem(now.AddMinutes(-5), ActivityKind.DeletedOnServer, "old/draft.txt", "Deleted on the server: old/draft.txt"));
        feed.Add(new ActivityItem(now.AddMinutes(-2), ActivityKind.Conflict, "Projects/report (conflict LAPTOP 2026-10-02 141509).docx",
            "Conflict on Projects/report.docx: your version was kept as report (conflict LAPTOP 2026-10-02 141509).docx"));
        feed.Add(new ActivityItem(now.AddSeconds(-20), ActivityKind.Uploaded, "Photos/holiday.jpg", "Uploaded: Photos/holiday.jpg"));
        window.ShowActivity(feed.Items);
        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = now.AddMinutes(-1),
            CurrentPath = "Videos/presentation.mp4", Operation = "upload", BytesDone = 640, BytesTotal = 1000, FilesDone = 2, FilesTotal = 5,
        }, "/Users/me/Work");
        window.ShowAttention([new AttentionItem("Conflict copy: report (conflict LAPTOP 2026-10-02 141509).docx",
            "Both computers changed this file. Compare the two, keep what you want, delete the other.", "Show in folder", () => { })]);
        Save(window, outDir, "main-window-syncing.png");

        window.ShowStatus(StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Blocked, BlockReason = BlockReason.MassDelete, PendingDeletes = 37, LastSyncAt = now.AddMinutes(-3),
            Text = "This pass would make 0 local and 37 server deletion(s) out of 120 tracked file(s); nothing was deleted.",
        }, "/Users/me/Work");
        Save(window, outDir, "main-window-blocked.png");

        window.ShowStatus(StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = now }, "/Users/me/Work");
        Save(window, outDir, "main-window-idle.png");
        window.AllowClose = true;
        window.Close();

        var settings = new SettingsWindow(new ClientSettings(), null, firstRun: true, autoStart: true) { Width = 580, Height = 480 };
        settings.Show();
        Save(settings, outDir, "settings-first-run.png");
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
