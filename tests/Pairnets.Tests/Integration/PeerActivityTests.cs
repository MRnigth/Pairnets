using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// What a computer hears about the other one through the server: that it has the change ("PeerSynced"), which the
/// overview shows as "Has your changes". Live speeds ("PeerTransfer") are each app's own reports: see LiveSpeedTests.
/// </summary>
public class PeerActivityTests : IAsyncLifetime
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
    public async Task EachComputerHearsThatTheOtherHasItsChange()
    {
        var desktop = _desktop.StartRunner();
        var laptop = _laptop.StartRunner();
        await WaitUntil(() => desktop.HubConnected && laptop.HubConnected, TimeSpan.FromSeconds(15), "hub connections");
        await WaitUntil(() => desktop.LastSyncAt is not null && laptop.LastSyncAt is not null, TimeSpan.FromSeconds(15), "startup passes");

        var syncedOnDesktop = new List<(string Device, long Version)>();
        long sent = 0;
        desktop.PeerSynced += (d, v) => { lock (syncedOnDesktop) syncedOnDesktop.Add((d, v)); };
        _desktop.Engine.ChangeSent += v => Interlocked.Exchange(ref sent, Math.Max(Interlocked.Read(ref sent), v));

        _desktop.WriteBytes("photos/big.bin", Enumerable.Range(0, 3 << 20).Select(i => (byte)i).ToArray());
        await WaitUntil(() => _laptop.Exists("photos/big.bin"), TimeSpan.FromSeconds(20), "laptop to download the file");

        // When the laptop is done, it says it has everything up to the change; the desktop never hears about itself.
        Assert.True(Interlocked.Read(ref sent) > 0);
        await WaitUntil(() => { lock (syncedOnDesktop) return syncedOnDesktop.Any(s => s.Device == "laptop" && s.Version >= Interlocked.Read(ref sent)); },
            TimeSpan.FromSeconds(10), "the laptop to say it has the change");
        lock (syncedOnDesktop)
            Assert.DoesNotContain(syncedOnDesktop, s => s.Device == "desktop");
    }

    [Fact]
    public async Task AComputerThatConnectsLaterStillHearsWhoHasWhat()
    {
        _desktop.Write("a.txt", "something to have"); // an empty server has no version to report
        await _desktop.SyncAsync();
        var laptop = _laptop.StartRunner();
        await WaitUntil(() => laptop.HubConnected && laptop.LastResult is { CaughtUp: true }, TimeSpan.FromSeconds(15), "the laptop's first sync");

        var synced = new List<(string Device, long Version)>();
        var desktop = _desktop.StartRunner();
        desktop.PeerSynced += (d, v) => { lock (synced) synced.Add((d, v)); };
        // The server tells a computer that connects what the others reported before.
        await WaitUntil(() => { lock (synced) return synced.Any(s => s.Device == "laptop"); }, TimeSpan.FromSeconds(15), "the replayed report");
    }
}
