using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// What a computer hears about the other one through the server: its real uploads and downloads with their speed
/// ("PeerTransfer"), and that it has the change ("PeerSynced"). The overview's line to the other computer draws this.
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
    public async Task EachComputerHearsTheOthersTransfersAndThatItHasTheChange()
    {
        var desktop = _desktop.StartRunner();
        var laptop = _laptop.StartRunner();
        await WaitUntil(() => desktop.HubConnected && laptop.HubConnected, TimeSpan.FromSeconds(15), "hub connections");
        await WaitUntil(() => desktop.LastSyncAt is not null && laptop.LastSyncAt is not null, TimeSpan.FromSeconds(15), "startup passes");

        var heardOnDesktop = new List<(string Device, PeerTransfer Transfer)>();
        var heardOnLaptop = new List<(string Device, PeerTransfer Transfer)>();
        var syncedOnDesktop = new List<(string Device, long Version)>();
        long sent = 0;
        desktop.PeerTransferChanged += (d, t) => { lock (heardOnDesktop) heardOnDesktop.Add((d, t)); };
        laptop.PeerTransferChanged += (d, t) => { lock (heardOnLaptop) heardOnLaptop.Add((d, t)); };
        desktop.PeerSynced += (d, v) => { lock (syncedOnDesktop) syncedOnDesktop.Add((d, v)); };
        _desktop.Engine.ChangeSent += v => Interlocked.Exchange(ref sent, Math.Max(Interlocked.Read(ref sent), v));

        // Big enough that the server counts it over a second or two.
        _desktop.Hooks.BeforeTransfer = (_, _) => Task.Delay(300);
        _laptop.Hooks.BeforeTransfer = (_, _) => Task.Delay(300);
        _desktop.WriteBytes("photos/big.bin", Enumerable.Range(0, 3 << 20).Select(i => (byte)i).ToArray());
        await WaitUntil(() => _laptop.Exists("photos/big.bin"), TimeSpan.FromSeconds(20), "laptop to download the file");

        // The laptop heard the desktop upload, and the desktop heard the laptop download (not a guess).
        await WaitUntil(() => { lock (heardOnLaptop) return heardOnLaptop.Any(h => h.Device == "desktop" && h.Transfer.Uploading); },
            TimeSpan.FromSeconds(5), "laptop to hear the upload");
        await WaitUntil(() => { lock (heardOnDesktop) return heardOnDesktop.Any(h => h.Device == "laptop" && h.Transfer.Downloading); },
            TimeSpan.FromSeconds(5), "desktop to hear the download");
        lock (heardOnDesktop)
        {
            Assert.Contains(heardOnDesktop, h => h.Device == "laptop" && h.Transfer.DownBytesPerSecond > 0);
            Assert.DoesNotContain(heardOnDesktop, h => h.Device == "desktop"); // never its own
        }

        // When the laptop is done: one report that it stopped, then that it has everything up to the change.
        await WaitUntil(() => { lock (heardOnDesktop) return heardOnDesktop.LastOrDefault(h => h.Device == "laptop").Transfer is { Active: false }; },
            TimeSpan.FromSeconds(10), "the download to be reported finished");
        Assert.True(Interlocked.Read(ref sent) > 0);
        await WaitUntil(() => { lock (syncedOnDesktop) return syncedOnDesktop.Any(s => s.Device == "laptop" && s.Version >= Interlocked.Read(ref sent)); },
            TimeSpan.FromSeconds(10), "the laptop to say it has the change");
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
