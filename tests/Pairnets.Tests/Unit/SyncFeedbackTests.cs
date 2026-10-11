using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// What the apps say while they sync (instead of a bare "Syncing"), the real speeds of every computer, and "too many
/// requests" as slowing down rather than failing.
/// </summary>
public class SyncFeedbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 21, 0, 0, TimeSpan.Zero);
    private const double MB = 1 << 20;

    private static StatusSnapshot Syncing => StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, Text = "Syncing", LastSyncAt = Now };

    // ------------------------------------------------------------------ status texts

    [Fact]
    public void CheckingFilesSaysHowFarItIs()
    {
        var listing = Syncing with { Stage = SyncStage.Checking };
        Assert.Equal("Checking files", listing.Headline);
        Assert.Equal("Listing the files in your folder", listing.DetailAt(Now));
        Assert.Equal("Checking files", listing.StatusLineAt(Now));

        var checking = listing with { CheckedFiles = 12_000, CheckTotal = 38_206 };
        Assert.Equal("Checking files", checking.Headline);
        Assert.Equal("12,000 of 38,206", checking.DetailAt(Now));
        Assert.Equal("Checking files · 12,000 of 38,206", checking.StatusLineAt(Now));
        Assert.False(checking.IsHeldUp); // it is working, not waiting

        var reading = Syncing with { Stage = SyncStage.ReadingServer };
        Assert.Equal("Reading the server's list", reading.Headline);
        Assert.Equal(string.Empty, reading.DetailAt(Now));
        Assert.Equal("Reading the server's list", reading.StatusLineAt(Now));
    }

    [Fact]
    public void TransfersSayTheDirectionTheSpeedAndTheTimeLeft()
    {
        var up = Syncing with
        {
            Stage = SyncStage.Transferring, CurrentPath = "a.jpg", Operation = "upload",
            Active = [new ActiveTransfer("a.jpg", "upload", 1, 2)],
            FilesDone = 37, FilesTotal = 1_204, PassBytesDone = 0, PassBytesTotal = 600L << 20,
            BytesPerSecond = 5 * MB, UpBytesPerSecond = 5 * MB,
        };
        Assert.Equal("Uploading", up.Headline);
        Assert.Equal("37 of 1,204 files · 5.00 MB/s · about 2 min left", up.DetailAt(Now));
        Assert.Equal("Uploading · 37 of 1,204 files · 5.00 MB/s · about 2 min left", up.StatusLineAt(Now));

        var down = up with { Operation = "download", Active = [new ActiveTransfer("a.jpg", "download", 1, 2)] };
        Assert.Equal("Downloading", down.Headline);
        var both = up with { Active = [new ActiveTransfer("a.jpg", "upload", 1, 2), new ActiveTransfer("b.jpg", "download", 1, 2)] };
        Assert.Equal("Uploading and downloading", both.Headline);
        Assert.Equal("1,204 files", up.BatchTitle.Split(' ', 2)[1]);
        Assert.Equal("37 of 1,204 files · 0 B of 600 MB", up.OverallText);
    }

    [Fact]
    public void WaitingForTheOtherComputerShowsItsRealProgress()
    {
        var waiting = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", WaitingFor = new PeerWait("PC-1", 38_206, 120) };
        Assert.True(waiting.IsWaiting);
        Assert.True(waiting.IsHeldUp);
        Assert.Equal("Waiting for PC-1 to finish uploading", waiting.Headline);
        // Without a live report: the changes that reached the server so far.
        Assert.Equal("Waiting for PC-1 to finish uploading · 120 of 38,206 files", waiting.StatusLineAt(Now));
        Assert.Equal("120 of 38,206 files are on the server", waiting.WaitingProgressAt(Now));

        // With PC-1's own report: its batch, as it counts it, and its speed.
        var live = waiting with
        {
            Peers = new Dictionary<string, PeerTransfer> { ["PC-1"] = new(new TransferReport(3.1 * MB, 0, 383, 38_206, 0, 0), Now.AddSeconds(-1)) },
        };
        Assert.Equal("Waiting for PC-1 to finish uploading · 383 of 38,206 files", live.StatusLineAt(Now));
        Assert.Equal("383 of 38,206 files are on the server · ↑ 3.10 MB/s", live.WaitingProgressAt(Now));
        Assert.Equal((383, 38_206), live.WaitingCounts(Now));

        // A report that went quiet no longer counts.
        Assert.Equal((120, 38_206), live.WaitingCounts(Now.AddSeconds(12)));

        // Checking its own folder meanwhile is said as it is.
        var checking = waiting with { Status = RunnerStatus.Syncing, Stage = SyncStage.Checking, CheckedFiles = 5, CheckTotal = 10 };
        Assert.False(checking.IsWaiting);
        Assert.Equal("Checking files · 5 of 10", checking.StatusLineAt(Now));
    }

    [Fact]
    public void TooManyRequestsSaysWhoAndHowLong()
    {
        var slowed = Syncing with { Stage = SyncStage.Transferring, SlowedUntil = Now.AddSeconds(29.2), SlowedBy = "sync.pairnets.app" };
        Assert.True(slowed.IsSlowedDown);
        Assert.True(slowed.IsHeldUp);
        Assert.Equal("Slowed down by the server", slowed.Headline);
        Assert.Equal("Too many requests to sync.pairnets.app; continuing in 30 s", slowed.DetailAt(Now));
        Assert.Equal("Too many requests to sync.pairnets.app; continuing in 30 s", slowed.StatusLineAt(Now));
        Assert.Equal("Too many requests to sync.pairnets.app; continuing in 1 s", slowed.StatusLineAt(Now.AddMinutes(1)));
        // Files wait in line, but nothing moves: the line stands still.
        var held = slowed with { Server = new ServerInfo("id", 1, 1), CurrentPath = "a", Operation = "upload", Active = [new ActiveTransfer("a", "upload", 0, 2)] };
        Assert.Equal(LinkFlow.None, DeviceMap.Build(held, "PC-1", Now).HereFlow);
        Assert.Equal(LinkFlow.Up, DeviceMap.Build(held with { SlowedUntil = null }, "PC-1", Now).HereFlow);

        // Paused wins: nothing goes on until Resume.
        var paused = slowed with { Status = RunnerStatus.Paused, Text = "Paused", Paused = true };
        Assert.False(paused.IsSlowedDown);
        Assert.Equal("Paused", paused.Headline);
        Assert.Equal("Paused", paused.StatusLineAt(Now));
        Assert.Equal(string.Empty, paused.DetailAt(Now));
    }

    [Fact]
    public void OtherStatesKeepTheirWords()
    {
        Assert.Equal("Up to date", (StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date" }).StatusLineAt(Now));
        Assert.Equal("Offline: timeout", (StatusSnapshot.Initial with { Status = RunnerStatus.Offline, Text = "Offline: timeout" }).StatusLineAt(Now));
        Assert.Equal("Syncing…", (Syncing with { Text = "Resuming" }).Headline); // never "Up to date" right after Resume
    }

    // ------------------------------------------------------------------ real speeds for every computer

    private static readonly DeviceInfo[] TwoPcs =
    [
        new("PC-1", Now.AddDays(-3), Now, true, "1.0.90", "Windows"),
        new("PC-2", Now.AddDays(-3), Now, true, "1.0.90", "Linux"),
    ];

    [Fact]
    public void ThePictureShowsEveryComputersRealSpeed()
    {
        var s = Syncing with
        {
            Server = new ServerInfo("id", 1, 1, "1.0.90", 412L << 30, 1L << 40),
            Devices = TwoPcs,
            Stage = SyncStage.Transferring, CurrentPath = "a", Operation = "upload", Active = [new ActiveTransfer("a", "upload", 1, 2)],
            FilesDone = 100, FilesTotal = 1_304, UpBytesPerSecond = 3.1 * MB, BytesPerSecond = 3.1 * MB,
            Peers = new Dictionary<string, PeerTransfer> { ["PC-2"] = new(new TransferReport(0, 4.2 * MB, 10, 10, 0, 0), Now.AddSeconds(-2)) },
        };
        var map = DeviceMap.Build(s, "PC-1", Now);
        Assert.Equal("↑ 3.10 MB/s · 1,204 files left", map.Here.Detail);
        Assert.Equal(LinkFlow.Up, map.HereFlow);
        Assert.Equal("PC-2", map.Other.Name);
        Assert.Equal("↓ 4.20 MB/s", map.Other.Detail);
        Assert.Equal(LinkFlow.Down, map.OtherFlow);

        // The Devices page says the same.
        var rows = DeviceRow.From(TwoPcs, "PC-1", Now, s);
        Assert.Equal(("PC-1 (this computer)", "↑ 3.10 MB/s · 1,204 files left"), (rows[0].Title, rows[0].StatusText));
        Assert.Equal(("PC-2", "↓ 4.20 MB/s"), (rows[1].Title, rows[1].StatusText));

        // Ten seconds without a report: no speed, the line stands still.
        var later = DeviceMap.Build(s, "PC-1", Now.AddSeconds(11));
        Assert.Equal("Online", later.Other.Detail);
        Assert.Equal(LinkFlow.None, later.OtherFlow);
        Assert.Equal("Online", DeviceRow.From(TwoPcs, "PC-1", Now.AddSeconds(11), s)[1].StatusText);

        // A report that says it stopped: no speed either.
        var stopped = s with { Peers = new Dictionary<string, PeerTransfer> { ["PC-2"] = new(TransferReport.Idle, Now) } };
        Assert.Equal(LinkFlow.None, DeviceMap.Build(stopped, "PC-1", Now).OtherFlow);

        // Idle computers show no speed, this one included.
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", Devices = TwoPcs, FilesDone = 5, FilesTotal = 9, UpBytesPerSecond = 0 };
        Assert.Null(idle.OwnLive);
        Assert.Equal("This computer", DeviceMap.Build(idle, "PC-1", Now).Here.Detail);
        Assert.Equal(["Online", "Online"], DeviceRow.From(TwoPcs, "PC-1", Now, idle).Select(r => r.StatusText));
    }

    [Fact]
    public void AStuckTransferIsReportedAsStalledWithItsDirectionAndItsLineStandsStill()
    {
        var uploading = Syncing with
        {
            Stage = SyncStage.Transferring, CurrentPath = "a", Operation = "upload", Active = [new ActiveTransfer("a", "upload", 1, 2)],
            FilesDone = 3, FilesTotal = 10, UpBytesPerSecond = 2 * MB, LastBytesAt = Now.AddSeconds(-1),
        };
        Assert.False(uploading.StalledAt(Now));
        Assert.True(uploading.StalledAt(Now.AddSeconds(5))); // no byte for 5 s
        Assert.True((uploading with { SlowedUntil = Now.AddSeconds(30) }).StalledAt(Now)); // held by "too many requests"
        Assert.False((uploading with { Active = [] }).StalledAt(Now.AddMinutes(1))); // nothing in flight: not stuck

        var own = (uploading with { UpBytesPerSecond = 0, Stalled = true }).OwnLive!;
        Assert.Equal((true, false, true), (own.Uploading, own.Downloading, own.Stalled));

        // The other computer's report says the same; its line stands still, it still counts as busy.
        var s = Syncing with
        {
            Server = new ServerInfo("id", 1, 1), Devices = TwoPcs,
            Peers = new Dictionary<string, PeerTransfer> { ["PC-2"] = new(new TransferReport(0, 0, 10, 640, 0, 0, Downloading: true, Stalled: true), Now) },
        };
        var map = DeviceMap.Build(s, "PC-1", Now);
        Assert.Equal(LinkFlow.None, map.OtherFlow);
        Assert.Equal("630 files left", map.Other.Detail);
        Assert.True(s.LiveOf("PC-2", "PC-1", Now)!.Stalled);
    }

    [Fact]
    public void ReportsAreCheckedAndSaidShortly()
    {
        var broken = new TransferReport(double.NaN, -5, 50, 10, -1, 100).Sanitized();
        Assert.Equal(new TransferReport(0, 0, 10, 10, 0, 100), broken);
        Assert.False(broken.IsActive);
        Assert.True(new TransferReport(0, 0, 1, 2, 0, 0).IsActive); // files still to go
        Assert.Equal(1, new TransferReport(0, 0, 1, 2, 0, 0).FilesLeft);
        Assert.Equal(100d * 1024 * 1024 * 1024, new TransferReport(1e30, 0, 0, 0, 0, 0).Sanitized().UpBytesPerSecond); // capped
        Assert.True(double.IsFinite(new TransferReport(double.PositiveInfinity, 0, 0, 0, 0, 0).Sanitized().UpBytesPerSecond));

        Assert.Equal("↑ 1.00 MB/s ↓ 2.00 MB/s", Format.Flow(MB, 2 * MB));
        Assert.Equal(string.Empty, Format.Flow(0, 0.5));
        Assert.Equal("1 file left", Format.Live(new TransferReport(0, 0, 1, 2, 0, 0)));
        Assert.Equal("38,206", Format.Count(38_206));
    }

    // ------------------------------------------------------------------ "too many requests"

    [Fact]
    public void TooManyRequestsHoldsEveryRequestThenLetsOneThroughAndRecovers()
    {
        var clock = new ManualClock(Now);
        var pressure = new BackPressure(clock);
        var heard = new List<DateTimeOffset?>();
        pressure.Changed += heard.Add;
        Assert.Null(pressure.HeldUntil);
        Assert.Equal(int.MaxValue, pressure.Limit);

        pressure.Refused(TimeSpan.FromSeconds(30));
        Assert.Equal(Now.AddSeconds(30), pressure.HeldUntil);
        Assert.Equal(1, pressure.Limit); // afterwards one request at a time
        Assert.Equal([Now.AddSeconds(30)], heard);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(pressure.HeldUntil);
        Assert.Equal(1, pressure.Limit);
        clock.Advance(BackPressure.RecoverStep);
        Assert.Equal(2, pressure.Limit); // doubles every quiet step...
        clock.Advance(BackPressure.RecoverStep);
        Assert.Equal(4, pressure.Limit);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(int.MaxValue, pressure.Limit); // ...until the limit is gone

        // Without Retry-After: 5 s, then 10 s while the 429s go on, back to 5 s after a normal answer.
        pressure.Answered();
        pressure.Refused(null);
        Assert.Equal(clock.Now.AddSeconds(5), pressure.HeldUntil);
        clock.Advance(TimeSpan.FromSeconds(5));
        pressure.Refused(null);
        Assert.Equal(clock.Now.AddSeconds(10), pressure.HeldUntil);
        clock.Advance(TimeSpan.FromSeconds(10));
        pressure.Answered();
        pressure.Refused(TimeSpan.Zero);
        Assert.Equal(clock.Now.AddSeconds(5), pressure.HeldUntil);
        pressure.Refused(TimeSpan.FromDays(3));
        Assert.Equal(clock.Now + BackPressure.MaxWait, pressure.HeldUntil); // a silly Retry-After is cut back
    }

    [Fact]
    public async Task HeldRequestsWaitTheirTurnAndThenGoOneAtATime()
    {
        var pressure = new BackPressure();
        pressure.Refused(TimeSpan.FromSeconds(1));
        var paused = 0;
        var started = DateTimeOffset.UtcNow;
        using (var slot = await pressure.EnterAsync(CancellationToken.None, () => paused++))
        {
            Assert.True(slot.Waited);
            Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(900));
            Assert.Equal(1, paused);

            // One runs; the next waits until it is done.
            var second = pressure.EnterAsync(CancellationToken.None).AsTask();
            await Task.Delay(200);
            Assert.False(second.IsCompleted);
            slot.Dispose();
            using var next = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(next.Waited);
        }

        // Cancelling a request that waits (Pause) ends its wait at once.
        pressure.Refused(TimeSpan.FromMinutes(1));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pressure.EnterAsync(cancel.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ------------------------------------------------------------------ checking files: progress, and Pause keeps what was read

    [Fact]
    public async Task CheckingReportsProgressAndAPauseKeepsTheFingerprintsReadSoFar()
    {
        using var folder = new TempDir("check");
        using var stateDir = new TempDir("check-state");
        for (var i = 0; i < 60; i++)
            await File.WriteAllTextAsync(Path.Combine(folder.Path, $"f{i:00}.txt"), "content " + i);
        foreach (var f in Directory.GetFiles(folder.Path))
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-5));
        using var state = new StateDb(Path.Combine(stateDir.Path, "state.db"));
        var scanner = new LocalScanner(folder.Path, new IgnoreList([]), state, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            ReportEvery = TimeSpan.Zero,
        };

        // Paused after 25 files: the 25 fingerprints are kept.
        using var pause = new CancellationTokenSource();
        var seen = new List<(int Done, int Total)>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(pause.Token, (done, total) =>
        {
            seen.Add((done, total));
            if (done == 25)
                pause.Cancel();
        }));
        Assert.Equal((0, 60), seen[0]); // the total is known before any file is read
        Assert.Equal((25, 60), seen[^1]);
        Assert.Equal(25, state.LoadFiles().Values.Count(f => f.Hash is not null));

        // The next check finishes and reports every step up to 60 of 60.
        seen.Clear();
        var result = await scanner.ScanAsync(CancellationToken.None, (done, total) => seen.Add((done, total)));
        Assert.Equal(60, result.Files.Count);
        Assert.Equal(Enumerable.Range(0, 61).Select(i => (i, 60)), seen);
        Assert.Equal(60, state.LoadFiles().Values.Count(f => f.Hash is not null));
    }

    [Fact]
    public async Task APassSaysWhatItDoesStepByStep()
    {
        var api = new FakeApi();
        api.Put("from-server.txt", "hello");
        using var folder = new TempDir("stages");
        using var stateDir = new TempDir("stages-state");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "mine.txt"), "mine");
        File.SetLastWriteTimeUtc(Path.Combine(folder.Path, "mine.txt"), DateTime.UtcNow.AddMinutes(-5));
        using var state = new StateDb(Path.Combine(stateDir.Path, "state.db"));
        var engine = new SyncEngine(new EngineOptions { Folder = folder.Path, DeviceName = "PC-1" }, api, state);
        var stages = new List<(SyncStage Stage, int Done, int Total)>();
        engine.StageChanged += (stage, done, total) =>
        {
            lock (stages)
                stages.Add((stage, done, total));
        };

        var result = await engine.RunPassAsync(new PassOptions("test"), CancellationToken.None);

        Assert.Equal(PassOutcome.Completed, result.Outcome);
        Assert.Equal(
            [(SyncStage.ReadingServer, 0, 0), (SyncStage.Checking, 0, 0), (SyncStage.Checking, 0, 1), (SyncStage.Checking, 1, 1), (SyncStage.Transferring, 0, 2)],
            stages);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(folder.Path, "from-server.txt")));
        Assert.Contains("mine.txt", api.LivePaths());
    }
}
