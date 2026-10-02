using Tether.Core.Hashing;
using Tether.Core.Paths;
using Tether.Core.Sync;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Faults;

/// <summary>
/// Network cuts, server restarts, locked files and a full disk. After every fault: no partial file
/// at a final path (client folder or server files/), state unchanged, and the next pass recovers.
/// </summary>
public class FaultInjectionTests : IAsyncLifetime
{
    private const int BigSize = 8 * 1024 * 1024;
    private TestServer _server = null!;
    private ChaosProxy _proxy = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _proxy = new ChaosProxy(_server.Port);
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private void AssertServerHasNoPartialFiles()
    {
        foreach (var f in Directory.EnumerateFiles(_server.Paths.Files, "*", SearchOption.AllDirectories))
        {
            var rel = PathRules.FromOsRelative(Path.GetRelativePath(_server.Paths.Files, f));
            var entry = _server.Store.Manifest.Get(rel);
            Assert.True(entry is { Deleted: false }, $"{rel} is in files/ but not live in the manifest");
            Assert.Equal(entry!.Hash, ContentHash.Of(File.ReadAllBytes(f)));
        }
    }

    /// <summary>
    /// The server deletes an aborted upload's temp file as soon as it notices the dead connection:
    /// immediately on a TCP reset, or (if the reset is not seen, as happens on macOS runners) when the
    /// upload stall timeout fires (2 s in tests). The deadline leaves room for slow runners.
    /// </summary>
    private static async Task WaitForEmptyTmp(string dir)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Directory.EnumerateFiles(dir).Any() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Empty(Directory.EnumerateFiles(dir));
    }

    [Fact]
    public async Task NetworkCutMidUploadLeavesNothingAndRecovers()
    {
        await using var desktop = new Device("desktop", _server, url: _proxy.Url);
        var data = Random(BigSize, 1);
        desktop.WriteBytes("big.bin", data);
        _proxy.CutAfterUpstreamBytes = 2 * 1024 * 1024;

        var r = await desktop.SyncAsync();
        Assert.Equal(PassOutcome.Offline, r.Outcome);
        Assert.True(_proxy.Cuts > 0);
        Assert.Null(_server.Store.Manifest.Get("big.bin"));
        Assert.False(File.Exists(Path.Combine(_server.Paths.Files, "big.bin")));
        await WaitForEmptyTmp(_server.Paths.Tmp);
        Assert.Null(desktop.State.GetFile("big.bin")?.BaseHash);

        _proxy.Heal();
        r = await desktop.SyncAsync();
        Assert.Equal(1, r.Uploaded);
        Assert.Equal(ContentHash.Of(data), _server.Store.Manifest.Get("big.bin")!.Hash);
        AssertServerHasNoPartialFiles();
    }

    [Fact]
    public async Task NetworkCutMidDownloadLeavesNoPartialFileAndRecovers()
    {
        await using var desktop = new Device("desktop", _server);
        await using var laptop = new Device("laptop", _server, url: _proxy.Url);
        var data = Random(BigSize, 2);
        desktop.WriteBytes("big.bin", data);
        await desktop.SyncAsync();
        await laptop.SyncAsync(); // creates the marker; big.bin is downloaded too...
        Assert.True(laptop.Exists("big.bin"));

        // ...so change it and cut the next download.
        var data2 = Random(BigSize, 3);
        desktop.WriteBytes("big.bin", data2);
        await desktop.SyncAsync();
        _proxy.Heal();
        _proxy.CutAfterDownstreamBytes = 2 * 1024 * 1024;
        var r = await laptop.SyncAsync();
        Assert.Equal(PassOutcome.Offline, r.Outcome);
        Assert.Equal(data, File.ReadAllBytes(laptop.Full("big.bin"))); // old content intact, never half-written
        Assert.Equal(ContentHash.Of(data), laptop.State.GetFile("big.bin")!.BaseHash);
        Assert.Empty(Directory.EnumerateFiles(laptop.Engine.TempDirectory));

        _proxy.Heal();
        r = await laptop.SyncAsync();
        Assert.Equal(1, r.Downloaded);
        Assert.Equal(data2, File.ReadAllBytes(laptop.Full("big.bin")));
    }

    [Fact]
    public async Task ServerRestartMidPassRecoversOnNextPass()
    {
        // One transfer at a time, so "the server stops after the first file" is exact.
        await using var desktop = new Device("desktop", _server, parallel: 1);
        for (var i = 0; i < 6; i++)
            desktop.WriteBytes($"f{i}.bin", Random(256 * 1024, 10 + i));
        var stopped = false;
        desktop.Hooks.AfterAction = async (_, _) =>
        {
            if (stopped)
                return;
            stopped = true;
            await _server.StopAsync();
        };

        var r = await desktop.SyncAsync();
        Assert.Equal(PassOutcome.Offline, r.Outcome);
        desktop.Hooks.AfterAction = null;
        await _server.RestartAsync();
        Assert.True(_server.Store.DetectDrift().IsClean);
        AssertServerHasNoPartialFiles();

        r = await desktop.SyncAsync();
        Assert.Equal(PassOutcome.Completed, r.Outcome);
        Assert.Equal(5, r.Uploaded);
        Assert.Equal(6, _server.Store.ReadManifest(null).Entries.Count);
        AssertServerHasNoPartialFiles();
        Assert.Equal(0, (await desktop.SyncAsync()).Changes);
    }

    [Fact]
    public async Task LockedFileIsSkippedAndSyncedLater()
    {
        await using var desktop = new Device("desktop", _server);
        await using var laptop = new Device("laptop", _server);
        desktop.Write("doc.txt", "v1");
        desktop.Write("other.txt", "o");
        await desktop.SyncAsync();
        await laptop.SyncAsync();

        // A locked new file is unknown (not uploaded), and a locked target is not overwritten.
        desktop.Write("doc.txt", "v2");
        await desktop.SyncAsync();
        desktop.Write("locked-new.txt", "new");
        using (new FileStream(desktop.Full("locked-new.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (new FileStream(laptop.Full("doc.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var dr = await desktop.SyncAsync();
            Assert.Equal(1, dr.Unstable);
            Assert.Null(_server.Store.Manifest.Get("locked-new.txt"));

            var lr = await laptop.SyncAsync();
            Assert.Equal(PassOutcome.Completed, lr.Outcome);
            Assert.Equal(1, lr.Errors + lr.Unstable);
            Assert.Equal(ContentHash.Of("v1"u8), laptop.State.GetFile("doc.txt")!.BaseHash);
        }
        Assert.Equal("v1", laptop.Read("doc.txt"));
        Assert.Empty(Directory.EnumerateFiles(laptop.Engine.TempDirectory));

        await desktop.SyncAsync();
        await laptop.SyncAsync();
        Assert.Equal("v2", laptop.Read("doc.txt"));
        Assert.Equal("new", laptop.Read("locked-new.txt"));
    }

    [Fact]
    public async Task DiskFullDuringDownloadLeavesNoPartialFileAndRecovers()
    {
        await using var desktop = new Device("desktop", _server);
        await using var laptop = new Device("laptop", _server);
        var data = Random(BigSize, 4);
        desktop.WriteBytes("big.bin", data);
        desktop.Write("small.txt", "s");
        await desktop.SyncAsync();

        laptop.Hooks.WrapDownloadStream = s => new DiskFullStream(s, failAfter: 1024 * 1024);
        var r = await laptop.SyncAsync();
        Assert.Equal(PassOutcome.Completed, r.Outcome);
        Assert.Equal(1, r.Errors); // big.bin failed...
        Assert.Equal(1, r.Downloaded); // ...but the small file still arrived (per-file isolation)
        Assert.False(laptop.Exists("big.bin"));
        Assert.Null(laptop.State.GetFile("big.bin")?.BaseHash);
        Assert.Empty(Directory.EnumerateFiles(laptop.Engine.TempDirectory));

        laptop.Hooks.WrapDownloadStream = null;
        r = await laptop.SyncAsync();
        Assert.Equal(1, r.Downloaded);
        Assert.Equal(data, File.ReadAllBytes(laptop.Full("big.bin")));
    }

    /// <summary>Write stream that fails like a full disk after N bytes.</summary>
    private sealed class DiskFullStream(Stream inner, long failAfter) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        private void Check(int count)
        {
            _written += count;
            if (_written > failAfter)
                throw new IOException("There is not enough space on the disk.");
        }
    }
}
