using Tether.Core.Sync;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Integration;

/// <summary>The background runner: file watcher, SignalR push and retries, against the real server.</summary>
public class RunnerTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private Device _desktop = null!;
    private Device _laptop = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _desktop = new Device("desktop", _server);
        _laptop = new Device("laptop", _server);
    }

    public async Task DisposeAsync()
    {
        await _desktop.DisposeAsync();
        await _laptop.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task PushMakesTheOtherDeviceSyncQuicklyAndOriginIgnoresItsOwnEcho()
    {
        var desktopReports = new List<PassReport>();
        var desktopEchoes = 0;
        var desktop = _desktop.StartRunner();
        desktop.PassCompleted += r => { lock (desktopReports) desktopReports.Add(r); };
        desktop.RemoteChangeReceived += (device, _) => { if (device == "desktop") Interlocked.Increment(ref desktopEchoes); };
        var laptop = _laptop.StartRunner();
        await WaitUntil(() => desktop.HubConnected && laptop.HubConnected, TimeSpan.FromSeconds(15), "hub connections");
        await WaitUntil(() => desktop.LastSyncAt is not null && laptop.LastSyncAt is not null, TimeSpan.FromSeconds(15), "startup passes");

        _desktop.Write("pushed.txt", "hello from desktop");
        await WaitUntil(() => _laptop.Exists("pushed.txt"), TimeSpan.FromSeconds(5), "laptop to receive the file");
        Assert.Equal("hello from desktop", _laptop.Read("pushed.txt"));

        // The desktop heard its own change echoed back but did not run a pass for it.
        await WaitUntil(() => Volatile.Read(ref desktopEchoes) > 0, TimeSpan.FromSeconds(5), "echo");
        await Task.Delay(500);
        lock (desktopReports)
            Assert.DoesNotContain(desktopReports, r => r.Reasons.Contains("remote-change"));

        // And the other way round.
        _laptop.Write("back.txt", "hello from laptop");
        await WaitUntil(() => _desktop.Exists("back.txt"), TimeSpan.FromSeconds(5), "desktop to receive the file");
    }

    [Fact]
    public async Task RunnerGoesOfflineAndRecoversWithoutLosingLocalChanges()
    {
        var runner = _desktop.StartRunner();
        await WaitUntil(() => runner.LastSyncAt is not null, TimeSpan.FromSeconds(15), "first pass");
        var port = _server.Port;
        await _server.StopAsync();

        _desktop.Write("offline-edit.txt", "written while offline");
        await WaitUntil(() => runner.Status == RunnerStatus.Offline, TimeSpan.FromSeconds(10), "offline status");

        await _server.RestartAsync();
        Assert.Equal(port, _server.Port);
        await WaitUntil(() => _server.Store.Manifest.Get("offline-edit.txt") is not null, TimeSpan.FromSeconds(15), "upload after reconnect");
        await WaitUntil(() => runner.Status == RunnerStatus.Idle, TimeSpan.FromSeconds(10), "idle status");
    }

    [Fact]
    public async Task BlockedPassReportsBlockedStatusAndApprovalRuns()
    {
        for (var i = 0; i < 10; i++)
            _desktop.Write($"f{i}.txt", "x");
        await _desktop.SyncAsync();
        for (var i = 0; i < 5; i++)
            _desktop.Delete($"f{i}.txt");
        var runner = _desktop.StartRunner();
        await WaitUntil(() => runner.Status == RunnerStatus.Blocked, TimeSpan.FromSeconds(10), "blocked status");
        Assert.Equal(5, runner.ApproveDeletions());
        await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Count(e => e.Deleted) == 5, TimeSpan.FromSeconds(10), "approved deletions");
    }

    [Fact]
    public async Task CatchUpIsAnnouncedAfterManyTransfers()
    {
        for (var i = 0; i < 12; i++)
            _desktop.Write($"c{i}.txt", i.ToString());
        await _desktop.SyncAsync();
        var announced = 0;
        var runner = _laptop.StartRunner();
        runner.CatchUpCompleted += n => Interlocked.Exchange(ref announced, n);
        await WaitUntil(() => Volatile.Read(ref announced) == 12, TimeSpan.FromSeconds(15), "catch-up event");
    }

    [Fact]
    public async Task PauseStopsARunningPassAndResumeFinishesIt()
    {
        var runner = _desktop.StartRunner();
        await WaitUntil(() => runner.LastSyncAt is not null, TimeSpan.FromSeconds(15), "first pass");

        var started = 0;
        _desktop.Hooks.BeforeTransfer = async (_, _) =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(150); // slow link: the pass takes a few seconds
        };
        for (var i = 0; i < 60; i++)
            _desktop.Write($"many/f{i:00}.txt", "file " + i);
        runner.RequestSync("test");
        await WaitUntil(() => Volatile.Read(ref started) >= 8, TimeSpan.FromSeconds(15), "uploads to start");

        runner.Pause();
        Assert.Equal(RunnerStatus.Paused, runner.Status);
        // In-flight requests end (slowly, on a busy machine); once nothing has started for a full second, nothing new may start.
        var lastStarted = -1;
        var quietSince = DateTime.UtcNow;
        var settleDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow - quietSince < TimeSpan.FromSeconds(1) && DateTime.UtcNow < settleDeadline)
        {
            await Task.Delay(100);
            var now = Volatile.Read(ref started);
            if (now != lastStarted)
            {
                lastStarted = now;
                quietSince = DateTime.UtcNow;
            }
        }
        var storedAfterPause = _server.Store.ReadManifest(null).Entries.Count;
        var startedAfterPause = Volatile.Read(ref started);
        await Task.Delay(1500);
        Assert.Equal(startedAfterPause, Volatile.Read(ref started));
        Assert.Equal(storedAfterPause, _server.Store.ReadManifest(null).Entries.Count);
        Assert.InRange(storedAfterPause, 1, 59);
        Assert.Equal(RunnerStatus.Paused, runner.Status);
        Assert.Empty(Directory.EnumerateFiles(_server.Paths.Tmp));

        _desktop.Hooks.BeforeTransfer = null;
        runner.Resume();
        await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Count == 60, TimeSpan.FromSeconds(20), "all uploads after resume");
        await WaitUntil(() => runner.Status == RunnerStatus.Idle, TimeSpan.FromSeconds(10), "idle status");
    }

    private static RunnerOptions SmallBatches(RunnerOptions o) => new()
    {
        ServerUrl = o.ServerUrl,
        Token = o.Token,
        DeviceId = o.DeviceId,
        WatcherDebounce = o.WatcherDebounce,
        WatcherMaxDelay = o.WatcherMaxDelay,
        RemoteDebounce = o.RemoteDebounce,
        PeriodicInterval = o.PeriodicInterval,
        UnstableRetry = o.UnstableRetry,
        OfflineBackoff = o.OfflineBackoff,
        BatchThreshold = 10,
        PeerQuietTimeout = TimeSpan.FromSeconds(2),
        HoldCheckInterval = TimeSpan.FromMilliseconds(200),
    };

    private async Task<(SyncRunner Desktop, SyncRunner Laptop)> StartBothAsync()
    {
        var desktop = _desktop.StartRunner(tweak: SmallBatches);
        var laptop = _laptop.StartRunner(tweak: SmallBatches);
        await WaitUntil(() => desktop.HubConnected && laptop.HubConnected, TimeSpan.FromSeconds(15), "hub connections");
        await WaitUntil(() => desktop.LastSyncAt is not null && laptop.LastSyncAt is not null, TimeSpan.FromSeconds(15), "startup passes");
        return (desktop, laptop);
    }

    [Fact]
    public async Task OtherComputerWaitsForABigBatchAndDownloadsItInOneGo()
    {
        var (desktop, laptop) = await StartBothAsync();
        var downloadsPerPass = new List<int>();
        laptop.PassCompleted += r => { lock (downloadsPerPass) downloadsPerPass.Add(r.Result.Downloaded); };
        PeerWait? sawWait = null;
        laptop.PeerWaitChanged += w => { if (w is not null) sawWait = w; };

        _desktop.Hooks.BeforeTransfer = (_, _) => Task.Delay(80);
        for (var i = 0; i < 30; i++)
            _desktop.Write($"batch/f{i:00}.txt", "batch " + i);
        desktop.RequestSync("test");

        await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Count(e => e.Path.StartsWith("batch/", StringComparison.Ordinal)) == 30,
            TimeSpan.FromSeconds(20), "desktop to upload the batch");
        await WaitUntil(() => Enumerable.Range(0, 30).All(i => _laptop.Exists($"batch/f{i:00}.txt")), TimeSpan.FromSeconds(15), "laptop to download it");
        // The files land on disk before the pass reports, so wait for the report too.
        await WaitUntil(() => { lock (downloadsPerPass) return downloadsPerPass.Sum() >= 30; }, TimeSpan.FromSeconds(10), "laptop's pass report");

        Assert.NotNull(sawWait);
        Assert.Equal("desktop", sawWait!.Device);
        Assert.Equal(30, sawWait.Count);
        lock (downloadsPerPass)
            Assert.Contains(30, downloadsPerPass); // in one go, not a few at a time
        Assert.Null(laptop.WaitingFor);
    }

    [Fact]
    public async Task WaitEndsWhenTheOtherComputerGoesQuietOrStops()
    {
        var (desktop, laptop) = await StartBothAsync();
        var gate = new TaskCompletionSource();
        var started = 0;
        _desktop.Hooks.BeforeTransfer = async (_, _) =>
        {
            if (Interlocked.Increment(ref started) > 8)
                await gate.Task; // the desktop's connection "hangs" after a few files
        };
        for (var i = 0; i < 20; i++)
            _desktop.Write($"slow/f{i:00}.txt", "slow " + i);
        desktop.RequestSync("test");

        await WaitUntil(() => laptop.WaitingFor is not null, TimeSpan.FromSeconds(10), "laptop to start waiting");
        // Nothing arrives for 2 s: the laptop stops waiting and fetches what is already there.
        await WaitUntil(() => laptop.WaitingFor is null, TimeSpan.FromSeconds(10), "quiet timeout");
        var onServer = _server.Store.ReadManifest(null).Entries.Count(e => e.Path.StartsWith("slow/", StringComparison.Ordinal));
        Assert.InRange(onServer, 1, 19);
        await WaitUntil(() => Directory.Exists(_laptop.Full("slow")) && Directory.EnumerateFiles(_laptop.Full("slow")).Count() >= onServer,
            TimeSpan.FromSeconds(10), "laptop to download the files already on the server");

        gate.SetResult();
        await WaitUntil(() => Enumerable.Range(0, 20).All(i => _laptop.Exists($"slow/f{i:00}.txt")), TimeSpan.FromSeconds(20), "the rest");
    }

    [Fact]
    public async Task DownloadNowAnywayEndsTheWait()
    {
        var (desktop, laptop) = await StartBothAsync();
        var gate = new TaskCompletionSource();
        _desktop.Hooks.BeforeTransfer = (_, _) => gate.Task;
        for (var i = 0; i < 12; i++)
            _desktop.Write($"held/f{i:00}.txt", "held " + i);
        desktop.RequestSync("test");
        await WaitUntil(() => laptop.WaitingFor is not null, TimeSpan.FromSeconds(10), "laptop to start waiting");
        laptop.ReleaseHold();
        Assert.Null(laptop.WaitingFor);
        gate.SetResult();
        await WaitUntil(() => Enumerable.Range(0, 12).All(i => _laptop.Exists($"held/f{i:00}.txt")), TimeSpan.FromSeconds(20), "all files");
    }
}
