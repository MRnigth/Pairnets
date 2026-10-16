using Pairnets.Core.Hashing;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Tests that measure the process's memory. <see cref="GC.GetTotalMemory"/> counts every test running at the same
/// time, so these run on their own, after the others.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasurements
{
    public const string Name = "Memory measurements";
}

/// <summary>A file over 100 MB goes up and comes down without being held in memory.</summary>
[Collection(MemoryMeasurements.Name)]
public class LargeFileStreamingTests : IAsyncLifetime
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

    [Fact]
    public async Task LargeFileIsStreamedBothWays()
    {
        const int size = 101 * 1024 * 1024;
        var rnd = new Random(42);
        var buffer = new byte[1024 * 1024];
        using (var fs = File.Create(_desktop.Full("big.bin")))
        {
            for (var written = 0; written < size; written += buffer.Length)
            {
                rnd.NextBytes(buffer);
                fs.Write(buffer, 0, Math.Min(buffer.Length, size - written));
            }
        }
        var expected = (await ContentHash.OfFileAsync(_desktop.Full("big.bin"))).Hash;
        // Measure memory still in use (after a full collection); no other test runs meanwhile (see MemoryMeasurements).
        var before = GC.GetTotalMemory(forceFullCollection: true);
        Assert.Equal(1, (await _desktop.SyncAsync()).Uploaded);
        Assert.Equal(1, (await _laptop.SyncAsync()).Downloaded);
        Assert.Equal(expected, (await ContentHash.OfFileAsync(_laptop.Full("big.bin"))).Hash);
        Assert.Equal(size, new FileInfo(_laptop.Full("big.bin")).Length);
        Assert.True(GC.GetTotalMemory(forceFullCollection: true) - before < 64L * 1024 * 1024, "transfer appears to keep the file in memory");
    }
}
