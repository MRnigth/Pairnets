using Microsoft.Extensions.Logging;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Logging;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// What keeps Pairnets from taking a whole Mac down (a process per notice, a full disk, loops that never wait, a folder
/// that holds Pairnets' own files) and what it leaves behind to find out why when it still does.
/// </summary>
public class MacSafetyTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-27T10:00:00Z");

    // ---- loops that never wait

    [Fact]
    public void RetriesWaitLongerEachTimeUpToACap()
    {
        var cap = TimeSpan.FromSeconds(60);
        var waits = Enumerable.Range(0, 7).Select(i => Backoff.Doubling(TimeSpan.FromSeconds(3), i, cap).TotalSeconds).ToArray();
        Assert.Equal([3d, 6, 12, 24, 48, 60, 60], waits);
    }

    [Fact]
    public void TheWatcherIsRestartedSlowerAndSlowerAndQuicklyAgainAfterAQuietSpell()
    {
        var policy = new WatcherRestartPolicy { QuietReset = TimeSpan.FromMinutes(10) };
        var waits = new List<double>();
        for (var i = 0; i < 7; i++)
            waits.Add(policy.Next(T0.AddSeconds(i * 2)).TotalSeconds);
        Assert.Equal([5d, 10, 30, 60, 300, 300, 300], waits);
        Assert.Equal(7, policy.Restarts);

        Assert.Equal(5, policy.Next(T0.AddMinutes(30)).TotalSeconds); // quiet for 10 minutes: starts over
    }

    // ---- a folder that holds too much

    [Fact]
    public void TheHomeFolderADrivesRootAndFoldersOfAllAccountsAreTooBroad()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var home = Path.Combine(root, "pn-home", "you");
        string[] broad = [home, Path.GetDirectoryName(home)!];
        string[] own = [Path.Combine(home, "AppData", "Pairnets")];

        Assert.NotNull(SyncFolderCheck.TooBroad(root, broad, own));
        Assert.NotNull(SyncFolderCheck.TooBroad(home, broad, own));
        Assert.NotNull(SyncFolderCheck.TooBroad(Path.GetDirectoryName(home)!, broad, own));
        Assert.NotNull(SyncFolderCheck.TooBroad(home + Path.DirectorySeparatorChar, broad, own));
    }

    [Fact]
    public void AFolderHoldingOrInsidePairnetsOwnFilesIsTooBroad()
    {
        var root = Path.Combine(Path.GetTempPath(), "pn-own");
        string[] own = [Path.Combine(root, "Library", "Application Support", "Pairnets")];

        Assert.NotNull(SyncFolderCheck.TooBroad(Path.Combine(root, "Library"), [], own)); // holds them
        Assert.NotNull(SyncFolderCheck.TooBroad(Path.Combine(root, "Library", "Application Support", "Pairnets", "logs"), [], own)); // inside
        Assert.Null(SyncFolderCheck.TooBroad(Path.Combine(root, "Documents", "Pairnets"), [], own));
        Assert.Null(SyncFolderCheck.TooBroad(Path.Combine(root, "Library2"), [], own));
    }

    [Fact]
    public void AFolderOfYourOwnIsFine()
    {
        Assert.Null(SyncFolderCheck.TooBroad(SyncFolderCheck.DefaultFolder));
        Assert.Null(SyncFolderCheck.Problem(Path.Combine(Path.GetTempPath(), "pairnets-tests", "own-folder")));
    }

    [Fact]
    public void TheRealHomeFolderIsRefusedWithAnExplanation()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var problem = SyncFolderCheck.Problem(home);
        Assert.NotNull(problem);
        Assert.Contains("too big", problem);
    }

    // ---- a full disk

    [Fact]
    public void ADownloadMustLeaveSomeSpaceFree()
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.True(DiskSpace.Allows(new DiskSpaceInfo(100 * gb, 500 * gb), 10 * gb));
        Assert.False(DiskSpace.Allows(new DiskSpaceInfo(11 * gb, 500 * gb), 10 * gb)); // would leave 1 GB of a 2 GB reserve
        Assert.False(DiskSpace.Allows(new DiskSpaceInfo(0, 500 * gb), 1));
        Assert.True(DiskSpace.Allows(new DiskSpaceInfo(5 * gb, 16 * gb), 4 * gb)); // small drive: the reserve is 5% of it (0.8 GB)
        Assert.True(DiskSpace.Allows(null, 100 * gb)); // not known: do not stop syncing for it
    }

    [Fact]
    public async Task ANestBiggerThanTheFreeSpaceIsNotDownloadedAndSaysSo()
    {
        await using var server = await TestServer.StartAsync();
        await using var desktop = new Device("desktop", server);
        await using var laptop = new Device("laptop", server);
        desktop.Write("a.txt", "one");
        desktop.Write("b.txt", "two");
        await desktop.SyncAsync();

        laptop.Hooks.DiskSpace = _ => new DiskSpaceInfo(1024, 500L * 1024 * 1024 * 1024); // 1 KB free
        var warnings = new List<PathWarningInfo>();
        laptop.Engine.PathWarningRaised += warnings.Add;
        var result = await laptop.SyncAsync();

        Assert.False(laptop.Exists("a.txt"));
        Assert.False(laptop.Exists("b.txt"));
        Assert.Equal(0, result.Downloaded);
        Assert.Single(warnings); // one warning for the pass, not one per file
        Assert.Equal(ErrorCodes.NoSpace, warnings[0].Code);
        Assert.False(Directory.Exists(laptop.Engine.TempDirectory) && Directory.EnumerateFiles(laptop.Engine.TempDirectory).Any());

        laptop.Hooks.DiskSpace = null; // space freed up: the next pass goes on by itself
        result = await laptop.SyncAsync();
        Assert.Equal(2, result.Downloaded);
        Assert.Equal("one", laptop.Read("a.txt"));
    }

    // ---- a process per notice

    [Fact]
    public async Task ThousandsOfNoticesShowAtMostAFewAndOneSummary()
    {
        var shown = new List<(string Title, string Text)>();
        using var queue = new NoticeQueue((t, x) => { lock (shown) shown.Add((t, x)); }, perWindow: 3, window: TimeSpan.FromMilliseconds(600));

        for (var i = 0; i < 2000; i++)
            queue.Add("File name not allowed", $"file {i}");

        await Eventually(() => { lock (shown) return shown.Count >= 3; });
        Assert.True(queue.Suppressed is > 1900 and <= 1997);

        await Eventually(() => { lock (shown) return shown.Count == 4; }); // the minute is over: one summary
        lock (shown)
        {
            Assert.Contains("more notices were held back", shown[3].Text);
            Assert.Equal("Pairnets", shown[3].Title);
        }
        Assert.Equal(0, queue.Suppressed);
        await Task.Delay(900);
        queue.Add("Next", "one"); // a new minute: notices show again
        await Eventually(() => { lock (shown) return shown.Count == 5; });
    }

    [Fact]
    public async Task NoticesAreShownOneAtATime()
    {
        var running = 0;
        var most = 0;
        var done = 0;
        using var queue = new NoticeQueue((_, _) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref done);
        }, perWindow: 10);
        for (var i = 0; i < 6; i++)
            queue.Add("t", i.ToString());
        await Eventually(() => Volatile.Read(ref done) == 6);
        Assert.Equal(1, most);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    // ---- the log can't fill the disk

    [Fact]
    public void ALogThatGrowsTooFastStopsAtItsCapAndKeepsWarnings()
    {
        using var dir = new TempDir("logcap");
        string file;
        using (var provider = new RollingFileLoggerProvider(dir.Path, minimum: LogLevel.Debug) { MaxDailyBytes = 2000 })
        {
            var log = provider.CreateLogger("T");
            for (var i = 0; i < 100; i++)
                log.LogInformation("line number {N} with some padding to make it longer ........", i);
            log.LogWarning("a warning after the cap");
            for (var i = 0; i < 1000; i++)
                log.LogError("errors go on until twice the cap {N} ................................", i);
            file = provider.CurrentFile;
        }
        var text = File.ReadAllText(file);
        Assert.Contains("a warning after the cap", text);
        Assert.Contains("today's log is full", text);
        Assert.DoesNotContain("line number 99", text);
        Assert.True(new FileInfo(file).Length < 2000 * 2 + 500);
    }

    // ---- finding out why

    [Fact]
    public void ARunThatNeverSaidGoodbyeIsNoticedAtTheNextStart()
    {
        using var dir = new TempDir("guard");
        var clock = new ManualClock(T0);

        var first = new StartGuard(dir.Path, clock).Begin();
        Assert.False(first.PreviousStoppedUnexpectedly);

        clock.Advance(TimeSpan.FromMinutes(5));
        var second = new StartGuard(dir.Path, clock).Begin(); // the first never called CleanExit
        Assert.True(second.PreviousStoppedUnexpectedly);
        Assert.Equal(1, second.RecentStops);
        Assert.False(second.CrashLoop);
        Assert.Equal(T0, second.LastAlive);
    }

    [Fact]
    public void ACleanQuitLeavesNothingToNotice()
    {
        using var dir = new TempDir("guard");
        var guard = new StartGuard(dir.Path);
        guard.Begin();
        guard.CleanExit();
        Assert.False(new StartGuard(dir.Path).Begin().PreviousStoppedUnexpectedly);
    }

    [Fact]
    public void TheLastTickSaysWhenItWasLastAlive()
    {
        using var dir = new TempDir("guard");
        var clock = new ManualClock(T0);
        var guard = new StartGuard(dir.Path, clock);
        guard.Begin();
        clock.Advance(TimeSpan.FromMinutes(7));
        guard.Tick();
        clock.Advance(TimeSpan.FromMinutes(1));

        var next = new StartGuard(dir.Path, clock).Begin();
        Assert.Equal(T0.AddMinutes(7), next.LastAlive);
    }

    [Fact]
    public void ThreeUnexpectedStopsInHalfAnHourAreACrashLoopAndForgivingEndsIt()
    {
        using var dir = new TempDir("guard");
        var clock = new ManualClock(T0);
        var result = new StartGuard(dir.Path, clock).Begin();
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            result = new StartGuard(dir.Path, clock).Begin();
        }
        Assert.Equal(3, result.RecentStops);
        Assert.True(result.CrashLoop);

        var guard = new StartGuard(dir.Path, clock);
        guard.Forgive();
        guard.Begin(); // (the run before it still counts as one stop)
        guard.CleanExit();
        Assert.False(new StartGuard(dir.Path, clock).Begin().CrashLoop);
    }

    [Fact]
    public void StopsOlderThanHalfAnHourDoNotCount()
    {
        using var dir = new TempDir("guard");
        var clock = new ManualClock(T0);
        new StartGuard(dir.Path, clock).Begin();
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            Assert.False(new StartGuard(dir.Path, clock).Begin().CrashLoop);
        }
    }

    [Fact]
    public void TheHealthLogWritesOneLineOfNumbersPerSampleAndKeepsTheNewestForTheReport()
    {
        using var dir = new TempDir("health");
        var extras = new HealthExtras(2, 1, 4, 0, 7, ["12.5GB"]);
        var ticks = 0;
        using var health = new HealthLog(dir.Path, () => extras, () => 3 * 1024 * 1024, () => ticks++);
        for (var i = 0; i < 5; i++)
            health.Sample();

        var lines = HealthLog.Tail(dir.Path, 3);
        Assert.Equal(3, lines.Count);
        Assert.Equal(5, ticks);
        Assert.Contains("mem=", lines[0]);
        Assert.Contains("threads=", lines[0]);
        Assert.Contains("files=", lines[0]);
        Assert.Contains("watchers=2 restarts=1 passes=4", lines[0]);
        Assert.Contains("held=7", lines[0]);
        Assert.Contains("log=3MB", lines[0]);
        Assert.Contains("free=12.5GB", lines[0]);
    }

    [Fact]
    public void MacReportsListPairnetsCrashesAndPanicsFromTheLastTwoWeeksOnly()
    {
        using var dir = new TempDir("macreports");
        var now = DateTimeOffset.UtcNow;
        var ips = "{\"app_name\":\"Pairnets\"}\n" + """
            {"exception":{"type":"EXC_BAD_ACCESS","signal":"SIGSEGV"},"termination":{"namespace":"SIGNAL","indicator":"Segmentation fault: 11"},
             "faultingThread":1,"usedImages":[{"name":"libobjc.A.dylib"},{"name":"Pairnets"}],
             "threads":[{"frames":[{"imageIndex":0,"symbol":"idle"}]},{"frames":[{"imageIndex":0,"symbol":"objc_msgSend"},{"imageIndex":1,"imageOffset":4242}]}]}
            """;
        File.WriteAllText(dir.Combine("Pairnets-2026-10-26-101010.ips"), ips);
        File.WriteAllText(dir.Combine("Other-2026-10-26-101010.ips"), "{}\n{}");
        File.WriteAllText(dir.Combine("panic-full-2026-10-26-111111.panic"), "header\npanicString: watchdog timeout: no checkins from watchdogd\nmore");
        File.WriteAllText(dir.Combine("JetsamEvent-2026-10-26-121212.ips"), "{}");
        var old = dir.Combine("Pairnets-2026-09-01-000000.ips");
        File.WriteAllText(old, ips);
        File.SetLastWriteTimeUtc(old, now.UtcDateTime.AddDays(-30));

        var text = MacReports.Build([dir.Path], now, force: true);

        Assert.Contains("Pairnets-2026-10-26-101010.ips", text);
        Assert.Contains("EXC_BAD_ACCESS SIGSEGV", text);
        Assert.Contains("Segmentation fault: 11", text);
        Assert.Contains("libobjc.A.dylib  objc_msgSend", text); // the frames of the thread that crashed
        Assert.DoesNotContain("idle", text);
        Assert.Contains("panic-full-2026-10-26-111111.panic", text);
        Assert.Contains("watchdog timeout", text);
        Assert.Contains("JetsamEvent-2026-10-26-121212.ips", text);
        Assert.DoesNotContain("Other-2026", text);
        Assert.DoesNotContain("2026-09-01", text);
    }

    [Fact]
    public void MacReportsSayNothingElsewhereAndSayWhenThereIsNothing()
    {
        using var dir = new TempDir("macreports");
        Assert.Contains("No Pairnets crash reports", MacReports.Build([dir.Path], force: true));
        if (!OperatingSystem.IsMacOS())
            Assert.Equal(string.Empty, MacReports.Build([dir.Path]));
    }

    [Fact]
    public async Task TheBugReportCarriesTheHealthLinesAndTheMacReportsNearTheTop()
    {
        using var dir = new TempDir("report");
        using var health = new HealthLog(dir.Path, () => new HealthExtras(1, 0, 0, 0, 0, []), () => 0);
        health.Sample();

        var report = await BugReport.BuildAsync(new ClientSettings(), null, null, null, "Mac app", healthDir: dir.Path, macReports: "panic-full-1.panic (2026-10-26 10:00)");

        Assert.Contains("## Health", report);
        Assert.Contains("watchers=1", report);
        Assert.Contains("## Mac reports", report);
        Assert.Contains("panic-full-1.panic", report);
        Assert.True(report.IndexOf("## Health", StringComparison.Ordinal) < report.IndexOf("## Settings", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWordsAboutAnUnexpectedStopAreTheSameInBothApps()
    {
        var loop = new StartGuardResult(true, T0, 3, true);
        Assert.Contains("3 times in the last half hour", UnexpectedStopText.Text(loop));
        Assert.Contains("paused", UnexpectedStopText.Text(loop));
        Assert.DoesNotContain("paused", UnexpectedStopText.Text(loop with { CrashLoop = false, RecentStops = 1 }));
        Assert.Contains("1 unexpected stop in", UnexpectedStopText.Card(loop with { RecentStops = 1 }));
    }
}
