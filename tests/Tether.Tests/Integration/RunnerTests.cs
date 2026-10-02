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
}
