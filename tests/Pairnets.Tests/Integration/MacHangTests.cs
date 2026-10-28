using System.Reflection;
using Avalonia;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Platform;
using Pairnets.Desktop.Views;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// The Mac beachball: a window that waits for the state database while the sync holds it, motion drawn into windows
/// nobody uses, OpenGL through Apple's Metal translation, and a copy that runs from the download.
/// </summary>
public class MacHangTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-27T10:00:00Z");
    private TestServer _server = null!;
    private readonly TempDir _folder = new("hang");
    private readonly TempDir _stateBase = new("hang-state");

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _folder.Dispose();
        _stateBase.Dispose();
    }

    // ---- the window never waits for the sync's database lock

    [Fact]
    public async Task TheWarningsTheWindowAsksForNeverWaitForTheDatabaseLock()
    {
        await using var session = ClientSession.Start(new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused-by-session",
            Folder = _folder.Path,
            DeviceName = "mac",
            FirstRunCompleted = true,
        }, _server.Token, new PermanentDeleteTrash(), stateBaseDir: _stateBase.Path, runnerOptions: o => new RunnerOptions
        {
            ServerUrl = o.ServerUrl,
            Token = o.Token,
            DeviceId = o.DeviceId,
            PeriodicInterval = TimeSpan.FromHours(1),
        });

        // The sync holds the database lock (it does while it loads or writes thousands of rows).
        var gate = typeof(StateDb).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.State)!;
        var holding = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));

        // A window redrawing four times a second asks again and again: none of it may wait.
        var asked = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
                _ = session.Warnings();
        });
        var finished = await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(5))) == asked;
        release.Set();
        await holder;

        Assert.True(finished, "Warnings() waited for the state database lock");
    }

    [Fact]
    public async Task AFileThatCanNotSyncShowsUpInTheWarningsAfterThePass()
    {
        File.WriteAllText(Path.Combine(_folder.Path, " bad name.txt"), "x");
        File.WriteAllText(Path.Combine(_folder.Path, "good.txt"), "y");
        await using var session = ClientSession.Start(new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused-by-session",
            Folder = _folder.Path,
            DeviceName = "mac",
            FirstRunCompleted = true,
        }, _server.Token, new PermanentDeleteTrash(), stateBaseDir: _stateBase.Path);

        for (var i = 0; i < 400 && !session.Warnings().Any(w => w.Path == " bad name.txt"); i++)
            await Task.Delay(50);

        Assert.Contains(session.Warnings(), w => w.Path == " bad name.txt");
        Assert.Single(session.Warnings());
    }

    // ---- motion only while a window is in use

    [Fact]
    public void MotionRunsWhileAWindowIsInUseAndStopsAFewSecondsAfterItIsNot()
    {
        var clock = new ManualClock(T0);
        var gate = new MotionGate(clock, TimeSpan.FromSeconds(6));
        var changes = 0;
        gate.Changed += () => changes++;

        Assert.False(gate.Live); // no window yet

        gate.Report(visible: true, active: true);
        Assert.True(gate.Live);

        gate.Report(visible: true, active: false); // another app came to the front
        Assert.True(gate.Live); // a moment of grace
        Assert.Equal(T0.AddSeconds(6), gate.EndsAt);

        clock.Advance(TimeSpan.FromSeconds(5));
        gate.Evaluate();
        Assert.True(gate.Live);

        clock.Advance(TimeSpan.FromSeconds(2));
        gate.Evaluate();
        Assert.False(gate.Live); // idle: the dots and spinners stop being drawn
        Assert.Null(gate.EndsAt);

        gate.Report(visible: true, active: true); // used again
        Assert.True(gate.Live);
        Assert.Equal(3, changes); // on, off, on
    }

    [Fact]
    public void MotionStopsAtOnceWhenNoWindowIsOnScreen()
    {
        var gate = new MotionGate(new ManualClock(T0), TimeSpan.FromSeconds(6));
        gate.Report(visible: true, active: true);
        Assert.True(gate.Live);

        gate.Report(visible: false, active: true); // hidden or minimized: the app lives in the menu bar
        Assert.False(gate.Live);
    }

    [Fact]
    public void AWindowShownWithoutBeingActivatedStillGetsAMomentOfMotion()
    {
        var clock = new ManualClock(T0);
        var gate = new MotionGate(clock, TimeSpan.FromSeconds(6));
        gate.Report(visible: true, active: false);
        Assert.True(gate.Live);

        clock.Advance(TimeSpan.FromSeconds(7));
        gate.Evaluate();
        Assert.False(gate.Live);
    }

    [Fact]
    public void TheMenuAndWindowAreRedrawnSlowlyWhenNobodyLooks()
    {
        Assert.True(RefreshPolicy.Due(tick: 3, inUse: true)); // a window in use: as soon as something changes
        Assert.False(RefreshPolicy.Due(tick: 3, inUse: false));
        Assert.False(RefreshPolicy.Due(tick: 7, inUse: false));
        Assert.True(RefreshPolicy.Due(tick: 8, inUse: false)); // every 2 seconds at most
        Assert.True(RefreshPolicy.Due(tick: 16, inUse: false));
    }

    // ---- how the Mac draws

    [Fact]
    public void TheMacDrawsWithMetalAndFallsBackToTheProcessor()
    {
        Assert.Equal([AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software], MacRendering.Modes(null));
        Assert.Equal([AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software], MacRendering.Modes("nonsense"));
        Assert.Equal([AvaloniaNativeRenderingMode.Software], MacRendering.Modes(" Software "));
        Assert.Equal([AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software], MacRendering.Modes("OpenGL"));
    }

    // ---- a copy that runs from the download

    [Theory]
    [InlineData("/private/var/folders/ab/cd/T/AppTranslocation/1234/d/Pairnets.app/Contents/MacOS/Pairnets", true)]
    [InlineData("/Volumes/Pairnets/Pairnets.app/Contents/MacOS/Pairnets", true)]
    [InlineData("/Applications/Pairnets.app/Contents/MacOS/Pairnets", false)]
    [InlineData("/Users/me/Applications/Pairnets.app/Contents/MacOS/Pairnets", false)]
    [InlineData("/Users/me/Downloads/Pairnets.app/Contents/MacOS/Pairnets", false)] // (macOS translocates that one; if it did not, it can update itself)
    [InlineData("/usr/local/bin/pairnets", false)]
    [InlineData(null, false)]
    public void ACopyRunningFromTheDownloadIsRecognised(string? program, bool fromDownload)
    {
        Assert.Equal(fromDownload, MacAppUpdate.RunsFromDownload(program));
    }

    [Fact]
    public void TheUpdateCardSaysToDragPairnetsToApplicationsAndOpenItThere()
    {
        var text = UpdateTexts.ByHand("1.0.61.11", UpdateTexts.MacNotInApplications);
        Assert.Contains("quit Pairnets", text);
        Assert.Contains("drag it to Applications", text);
        Assert.Contains("open it from there", text);
        Assert.Contains("updates by itself", text);
        Assert.Contains("1.0.61.11", text);

        Assert.Contains("Applications folder", UpdateTexts.MacMoveText);
        Assert.Contains("start when you log in", UpdateTexts.MacMoveText);
        Assert.Equal("Move Pairnets to Applications", UpdateTexts.MacMoveTitle);
    }

    [Fact]
    public void TheMoveNoteIsShownOncePerVersionAndOnlyForACopyRunningFromTheDownload()
    {
        using var dir = new TempDir("move-note");
        const string translocated = "/private/var/folders/ab/cd/T/AppTranslocation/1234/d/Pairnets.app/Contents/MacOS/Pairnets";

        Assert.False(MacAppUpdate.MoveNoticeDue(dir.Path, "/Applications/Pairnets.app/Contents/MacOS/Pairnets", "1.0.61.11"));
        Assert.True(MacAppUpdate.MoveNoticeDue(dir.Path, translocated, "1.0.61.11"));
        Assert.False(MacAppUpdate.MoveNoticeDue(dir.Path, translocated, "1.0.61.11")); // said once
        Assert.True(MacAppUpdate.MoveNoticeDue(dir.Path, translocated, "1.0.61.12")); // a new version: once more
    }

    [Fact]
    public void ALoginItemIsNeverWrittenWithADownloadPathAndABrokenOneIsRemoved()
    {
        using var dir = new TempDir("login-item");
        var agent = dir.Combine("app.pairnets.client.plist");
        const string translocated = "/private/var/folders/ab/cd/T/AppTranslocation/1234/d/Pairnets.app/Contents/MacOS/Pairnets";
        File.WriteAllText(agent, MacPlatform.AgentPlist(translocated));

        MacPlatform.RemoveBrokenLoginItem(agent);
        Assert.False(File.Exists(agent));

        File.WriteAllText(agent, MacPlatform.AgentPlist("/Applications/Pairnets.app/Contents/MacOS/Pairnets"));
        MacPlatform.RemoveBrokenLoginItem(agent);
        Assert.True(File.Exists(agent)); // a good one stays
        Assert.Contains("/Applications/Pairnets.app/Contents/MacOS/Pairnets", File.ReadAllText(agent));
        Assert.Contains("--autostart", File.ReadAllText(agent));
    }

    [Fact]
    public void ThePlatformRefusesTheLoginItemOnlyWhileRunningFromTheDownload()
    {
        // (the real Mac platform asks the running program's path; here that is the test runner, which is not a download)
        Assert.Null(new MacPlatform().AutoStartRefusedReason);
        Assert.Contains("Applications", UpdateTexts.MacLoginNeedsApplications);
    }

    // ---- being able to see the next beachball

    [Fact]
    public void TheHealthLineShowsTheLongestStallOfTheWindowThread()
    {
        var clock = new ManualClock(T0);
        var queue = new Queue<Action>();
        using var meter = new UiStallMeter(queue.Enqueue, clock);

        meter.Probe(); // sent at 10:00:00, the window thread is busy
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(4000, meter.TakeLongestMs()); // the wait so far counts, the beachball is visible before it ends

        meter.Probe(); // still waiting for the first: no second one piles up
        Assert.Single(queue);

        clock.Advance(TimeSpan.FromSeconds(1));
        queue.Dequeue()(); // the thread is free at last: 5 s in all
        Assert.Equal(5000, meter.TakeLongestMs());
        Assert.Equal(0, meter.TakeLongestMs()); // starts again

        meter.Probe();
        clock.Advance(TimeSpan.FromMilliseconds(30));
        queue.Dequeue()();
        Assert.Equal(30, meter.TakeLongestMs());

        var line = HealthLog.Line(T0, new HealthExtras(1, 0, 0, 0, 0, [], 5000), 0);
        Assert.Contains("ui-stall=5000ms", line);
        Assert.DoesNotContain("ui-stall", HealthLog.Line(T0, new HealthExtras(1, 0, 0, 0, 0, []), 0));
    }

    [Fact]
    public void MacReportsShowASpinOrCpuReportFromMacOSWithItsHeaderAndHeaviestStack()
    {
        using var dir = new TempDir("macreports-hang");
        var now = DateTimeOffset.UtcNow;
        var report = string.Join('\n',
            "Date/Time:        2026-10-20 11:12:55.595 +0200",
            "Event:            cpu usage",
            "Action taken:     none",
            "CPU:              90 seconds cpu time over 178 seconds (51% cpu average), exceeding limit of 50% cpu over 180 seconds",
            "Hardware model:   Mac16,8",
            "Note:             Translocated",
            "Heaviest stack for the target process:",
            "  19  thread_start + 8 (libsystem_pthread.dylib + 7040)",
            "  9   AvnGlRenderingSession::~AvnGlRenderingSession() + 20 (libAvalonia.Native.OSX.dylib + 40544)",
            "Powerstats for:   Pairnets [23251]",
            "Footprint:        444.27 MB");
        File.WriteAllText(dir.Combine("Pairnets_2026-10-20-111553_MacBook-Pro.cpu_resource.diag"), report);
        File.WriteAllText(dir.Combine("Pairnets_2026-10-20-120000.hang"), "Event: hang\nDuration: 12.5s\nHeaviest stack for the main thread of the target process:\n  12 main + 184 (Pairnets + 37800)");

        var text = MacReports.Build([dir.Path], now, force: true);

        Assert.Contains("cpu_resource.diag", text);
        Assert.Contains("Event:            cpu usage", text);
        Assert.Contains("51% cpu average", text);
        Assert.Contains("Note:             Translocated", text);
        Assert.Contains("AvnGlRenderingSession", text);
        Assert.Contains("Pairnets_2026-10-20-120000.hang", text);
        Assert.Contains("Duration: 12.5s", text);
        Assert.Contains("main + 184", text);
    }
}
