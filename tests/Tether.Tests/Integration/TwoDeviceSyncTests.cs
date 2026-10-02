using System.Text;
using Tether.Core.Hashing;
using Tether.Core.State;
using Tether.Core.Sync;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Integration;

/// <summary>Desktop and laptop syncing through the real in-process server.</summary>
public class TwoDeviceSyncTests : IAsyncLifetime
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

    private static void Completed(PassResult r) => Assert.True(r.Outcome == PassOutcome.Completed, r.ToString());

    [Fact]
    public async Task NewFilesNestedUnicodeAndEmptyReachTheOtherDevice()
    {
        _desktop.Write("top.txt", "top");
        _desktop.Write("a/b/c/deep.txt", "deep");
        _desktop.Write("Ünïcødé 日本/файл 😀.md", "unicode");
        _desktop.WriteBytes("empty.bin", []);

        var up = await _desktop.SyncAsync();
        Completed(up);
        Assert.Equal(4, up.Uploaded);
        var down = await _laptop.SyncAsync();
        Completed(down);
        Assert.Equal(4, down.Downloaded);

        Assert.Equal(_desktop.Snapshot(), _laptop.Snapshot());
        Assert.Equal(0, new FileInfo(_laptop.Full("empty.bin")).Length);
        Assert.Equal(File.GetLastWriteTimeUtc(_desktop.Full("a/b/c/deep.txt")).Ticks / 10_000,
            File.GetLastWriteTimeUtc(_laptop.Full("a/b/c/deep.txt")).Ticks / 10_000);
        Assert.Equal(0, (await _desktop.SyncAsync()).Changes);
        Assert.Equal(0, (await _laptop.SyncAsync()).Changes);
    }

    [Fact]
    public async Task EditsPropagateBackAndForthRepeatedly()
    {
        _desktop.Write("doc.txt", "v0");
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();
        for (var i = 1; i <= 6; i++)
        {
            var (writer, reader) = i % 2 == 1 ? (_laptop, _desktop) : (_desktop, _laptop);
            writer.Write("doc.txt", $"edit {i} by {writer.Name}");
            Assert.Equal(1, (await writer.SyncAsync()).Uploaded);
            Assert.Equal(1, (await reader.SyncAsync()).Downloaded);
            Assert.Equal($"edit {i} by {writer.Name}", reader.Read("doc.txt"));
        }
        Assert.Equal(6, _server.Store.ListHistory("doc.txt").Count);
    }

    [Fact]
    public async Task DeletionPropagatesAndPrunesEmptyFolders()
    {
        for (var i = 0; i < 5; i++)
            _desktop.Write($"keep{i}.txt", i.ToString());
        _desktop.Write("folder/only.txt", "x");
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();
        Assert.True(_laptop.Exists("folder/only.txt"));

        _desktop.Delete("folder/only.txt");
        Directory.Delete(_desktop.Full("folder"));
        Assert.Equal(1, (await _desktop.SyncAsync()).DeletedRemote);
        var r = await _laptop.SyncAsync();
        Assert.Equal(1, r.DeletedLocal);
        Assert.False(_laptop.Exists("folder/only.txt"));
        Assert.False(Directory.Exists(_laptop.Full("folder")));
        Assert.Single(_server.Store.ListHistory("folder/only.txt"));
    }

    [Fact]
    public async Task ConcurrentEditsKeepBothVersionsOnBothDevices()
    {
        _desktop.Write("shared.txt", "base");
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();

        _desktop.Write("shared.txt", "desktop version");
        _laptop.Write("shared.txt", "laptop version");
        await _desktop.SyncAsync();
        var r = await _laptop.SyncAsync();
        Assert.Equal(1, r.Conflicts);
        var copy = Assert.Single(r.ConflictCopies).ConflictCopyPath;
        Assert.Contains("(conflict laptop ", copy);

        await _desktop.SyncAsync();
        Assert.Equal("desktop version", _laptop.Read("shared.txt"));
        Assert.Equal("laptop version", _laptop.Read(copy));
        Assert.Equal("laptop version", _desktop.Read(copy));
        Assert.Equal(_desktop.Snapshot(), _laptop.Snapshot());
        Assert.Equal(0, (await _laptop.SyncAsync()).Changes);
    }

    [Fact]
    public async Task EditBeatsDeleteInBothDirections()
    {
        for (var i = 0; i < 10; i++)
            _desktop.Write($"pad{i}.txt", "p");
        _desktop.Write("a.txt", "a0");
        _desktop.Write("b.txt", "b0");
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();

        // a: deleted on desktop, edited on laptop. b: edited on desktop, deleted on laptop.
        _desktop.Delete("a.txt");
        _desktop.Write("b.txt", "b1");
        _laptop.Write("a.txt", "a1");
        _laptop.Delete("b.txt");
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();

        foreach (var d in new[] { _desktop, _laptop })
        {
            Assert.Equal("a1", d.Read("a.txt"));
            Assert.Equal("b1", d.Read("b.txt"));
        }
    }

    [Fact]
    public async Task FirstSyncWithIdenticalFoldersTransfersNothing()
    {
        for (var i = 0; i < 20; i++)
        {
            _desktop.Write($"d{i % 3}/f{i}.txt", "content " + i);
            _laptop.Write($"d{i % 3}/f{i}.txt", "content " + i);
        }
        await _desktop.SyncAsync();
        var r = await _laptop.SyncAsync();
        Completed(r);
        Assert.Equal(0, r.Changes);
        Assert.Equal(20, r.Recorded);
        Assert.Equal(20, _laptop.State.CountTracked());
    }

    [Fact]
    public async Task IgnoredFilesAreNeverUploaded()
    {
        _desktop.Write("~$doc.docx", "lock");
        _desktop.Write("x.tmp", "tmp");
        _desktop.Write("Thumbs.db", "t");
        _desktop.Write(".git/HEAD", "ref");
        _desktop.Write("dl.crdownload", "partial");
        _desktop.Write("real.txt", "real");
        var r = await _desktop.SyncAsync();
        Assert.Equal(1, r.Uploaded);
        Assert.Equal(["real.txt"], _server.Store.ReadManifest(null).Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task FileBeingWrittenIsSkippedNotUploadedNorDeleted()
    {
        await using var slow = new Device("slow", _server, stability: TimeSpan.FromSeconds(2));
        for (var i = 0; i < 5; i++)
            slow.Write($"f{i}.txt", "x");
        File.SetLastWriteTimeUtc(slow.Full("f0.txt"), DateTime.UtcNow.AddMinutes(-1));
        // f1..f4 are "just written": only f0 is stable.
        for (var i = 1; i < 5; i++)
            File.SetLastWriteTimeUtc(slow.Full($"f{i}.txt"), DateTime.UtcNow.AddMinutes(-1));
        await slow.SyncAsync();
        Assert.Equal(5, _server.Store.ReadManifest(null).Entries.Count);

        slow.Write("growing.bin", "partial");
        slow.Write("f0.txt", "being rewritten");
        var r = await slow.SyncAsync();
        Assert.Equal(2, r.Unstable);
        Assert.Equal(0, r.Changes);
        Assert.True(r.WantsRetry);
        var entries = _server.Store.ReadManifest(null).Entries;
        Assert.DoesNotContain(entries, e => e.Path == "growing.bin");
        Assert.False(entries.Single(e => e.Path == "f0.txt").Deleted);

        await Task.Delay(2200);
        r = await slow.SyncAsync();
        Assert.Equal(2, r.Uploaded);
    }

    [Fact]
    public async Task MassDeleteIsBlockedUntilAllowedOnce()
    {
        for (var i = 0; i < 20; i++)
            _desktop.Write($"f{i:00}.txt", i.ToString());
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();

        // 20 tracked: more than min(4, 50) deletions on one side is blocked.
        for (var i = 0; i < 10; i++)
            _desktop.Delete($"f{i:00}.txt");
        var r = await _desktop.SyncAsync();
        Assert.Equal(PassOutcome.Blocked, r.Outcome);
        Assert.Equal(BlockReason.MassDelete, r.BlockReason);
        Assert.Equal(10, r.BlockedDeletes.Count);
        Assert.All(r.BlockedDeletes, d => Assert.Equal(DeleteSide.Remote, d.Side));
        Assert.Equal(20, _server.Store.ReadManifest(null).Entries.Count(e => !e.Deleted));
        Assert.Equal(PassOutcome.Blocked, (await _desktop.SyncAsync()).Outcome); // still blocked

        Assert.Equal(10, _desktop.Engine.ApproveBlockedDeletions());
        r = await _desktop.SyncAsync();
        Assert.Equal(10, r.DeletedRemote);

        // The laptop now sees 10 server deletions out of 20 tracked: blocked on its side too.
        r = await _laptop.SyncAsync();
        Assert.Equal(BlockReason.MassDelete, r.BlockReason);
        Assert.All(r.BlockedDeletes, d => Assert.Equal(DeleteSide.Local, d.Side));
        Assert.Equal(20, _laptop.Files().Count);
        _laptop.Engine.ApproveBlockedDeletions();
        r = await _laptop.SyncAsync();
        Assert.Equal(10, r.DeletedLocal);

        // The approval covered exactly one pass.
        for (var i = 10; i < 20; i++)
            _desktop.Delete($"f{i:00}.txt");
        Assert.Equal(PassOutcome.Blocked, (await _desktop.SyncAsync()).Outcome);
    }

    [Fact]
    public async Task ApprovalDoesNotCoverNewDeletions()
    {
        for (var i = 0; i < 10; i++)
            _desktop.Write($"f{i}.txt", i.ToString());
        await _desktop.SyncAsync();
        for (var i = 0; i < 3; i++)
            _desktop.Delete($"f{i}.txt");
        Assert.Equal(PassOutcome.Blocked, (await _desktop.SyncAsync()).Outcome);
        _desktop.Engine.ApproveBlockedDeletions();
        _desktop.Delete("f5.txt"); // not part of what the user approved
        var r = await _desktop.SyncAsync();
        Assert.Equal(PassOutcome.Blocked, r.Outcome);
        Assert.Equal(4, r.BlockedDeletes.Count);
    }

    [Fact]
    public async Task EmptiedFolderIsRefused()
    {
        for (var i = 0; i < 3; i++)
            _desktop.Write($"f{i}.txt", i.ToString());
        await _desktop.SyncAsync();
        foreach (var f in _desktop.Files())
            _desktop.Delete(f);
        var r = await _desktop.SyncAsync();
        Assert.Equal(BlockReason.FolderEmpty, r.BlockReason);
        Assert.Equal(3, _server.Store.ReadManifest(null).Entries.Count(e => !e.Deleted));
    }

    [Fact]
    public async Task MissingMarkerOrMissingFolderIsRefused()
    {
        for (var i = 0; i < 3; i++)
            _desktop.Write($"f{i}.txt", i.ToString());
        await _desktop.SyncAsync();

        // Same path, but now a different (empty) folder, as if a drive were swapped.
        var moved = _desktop.Folder + "-moved";
        Directory.Move(_desktop.Folder, moved);
        var r = await _desktop.SyncAsync();
        Assert.Equal(BlockReason.FolderMissing, r.BlockReason);
        Directory.CreateDirectory(_desktop.Folder);
        _desktop.Write("unrelated.txt", "u");
        r = await _desktop.SyncAsync();
        Assert.Equal(BlockReason.MarkerMissing, r.BlockReason);
        Assert.Equal(3, _server.Store.ReadManifest(null).Entries.Count(e => !e.Deleted));
        Assert.DoesNotContain(_server.Store.ReadManifest(null).Entries, e => e.Path == "unrelated.txt");

        Directory.Delete(_desktop.Folder, true);
        Directory.Move(moved, _desktop.Folder);
        Completed(await _desktop.SyncAsync());
    }

    [Fact]
    public async Task ForeignMarkerNeedsConfirmation()
    {
        _desktop.Write("a.txt", "a");
        await _desktop.SyncAsync();
        // A second state database pointed at the same folder (e.g. state was reset).
        await using var other = new Device("desktop", _server);
        foreach (var f in Directory.EnumerateFiles(_desktop.Folder, "*", new EnumerationOptions { AttributesToSkip = 0 }))
            File.Copy(f, Path.Combine(other.Folder, Path.GetFileName(f)));
        var r = await other.SyncAsync();
        Assert.Equal(BlockReason.ForeignMarker, r.BlockReason);
        other.Engine.AdoptExistingMarker();
        r = await other.SyncAsync();
        Completed(r);
        Assert.Equal(0, r.Changes);
    }

    [Fact]
    public async Task ServerHistoryKeepsDeletedAndOverwrittenFilesAndRestoreReachesDevices()
    {
        for (var i = 0; i < 5; i++)
            _desktop.Write($"pad{i}.txt", "p");
        _desktop.Write("h.txt", "first");
        await _desktop.SyncAsync();
        _desktop.Write("h.txt", "second");
        await _desktop.SyncAsync();
        _desktop.Delete("h.txt");
        await _desktop.SyncAsync();

        var versions = _server.Store.ListHistory("h.txt");
        Assert.Equal(2, versions.Count);
        var firstId = versions.Single(v => v.Hash8 == ContentHash.Of(Encoding.UTF8.GetBytes("first"))[..8]).Id;
        var restored = await _desktop.Api.RestoreAsync("h.txt", firstId, default);
        Assert.Equal(Tether.Core.Api.ApiOutcome.Ok, restored.Outcome);

        await _desktop.SyncAsync();
        await _laptop.SyncAsync();
        Assert.Equal("first", _desktop.Read("h.txt"));
        Assert.Equal("first", _laptop.Read("h.txt"));
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
        var before = GC.GetTotalMemory(false);
        Assert.Equal(1, (await _desktop.SyncAsync()).Uploaded);
        Assert.Equal(1, (await _laptop.SyncAsync()).Downloaded);
        Assert.Equal(expected, (await ContentHash.OfFileAsync(_laptop.Full("big.bin"))).Hash);
        Assert.Equal(size, new FileInfo(_laptop.Full("big.bin")).Length);
        Assert.True(GC.GetTotalMemory(false) - before < 64L * 1024 * 1024, "transfer appears to buffer the file in memory");
    }

    [Fact]
    public async Task WrongTokenFailsThePassWithoutTouchingAnything()
    {
        await using var bad = new Device("intruder", _server, token: "not-the-right-token-at-all");
        bad.Write("x.txt", "x");
        var r = await bad.SyncAsync();
        Assert.Equal(PassOutcome.AuthFailed, r.Outcome);
        Assert.Empty(_server.Store.ReadManifest(null).Entries);
        Assert.True(bad.Exists("x.txt"));
    }

    [Fact]
    public async Task CaseCollisionBecomesPersistentWarning()
    {
        _desktop.Write("Report.txt", "desktop");
        await _desktop.SyncAsync();
        _laptop.Write("report.txt", "laptop");
        var warnings = new List<PathWarningInfo>();
        _laptop.Engine.PathWarningRaised += warnings.Add;

        var r = await _laptop.SyncAsync();
        Assert.Equal(1, r.Warnings);
        var w = Assert.Single(warnings);
        Assert.Equal("case-collision", w.Code);
        Assert.Equal("report.txt", w.Path);

        // Not retried while nothing changed...
        r = await _laptop.SyncAsync();
        Assert.Single(warnings);
        // The server keeps exactly one of the two names: the first one, with its content.
        var live = _server.Store.ReadManifest(null).Entries.Where(e => !e.Deleted).ToList();
        Assert.Contains(live, e => e.Path == "Report.txt" && e.Hash == ContentHash.Of(Encoding.UTF8.GetBytes("desktop")));
        Assert.DoesNotContain(live, e => e.Path == "report.txt");
    }

    [Fact]
    public async Task TransfersRunFourAtATimeAndAllArrive()
    {
        await _desktop.SyncAsync();
        await _laptop.SyncAsync();
        for (var i = 0; i < 24; i++)
            _desktop.Write($"batch/f{i:00}.txt", "content " + i);

        int inFlight = 0, peak = 0;
        async Task Track(SyncAction _, string __)
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }
            await Task.Delay(30);
            Interlocked.Decrement(ref inFlight);
        }
        _desktop.Hooks.BeforeTransfer = Track;
        var up = await _desktop.SyncAsync();
        Assert.Equal(24, up.Uploaded);
        Assert.Equal(4, peak);

        peak = 0;
        _laptop.Hooks.BeforeTransfer = Track;
        var down = await _laptop.SyncAsync();
        Assert.Equal(24, down.Downloaded);
        Assert.Equal(4, peak);
        for (var i = 0; i < 24; i++)
            Assert.Equal("content " + i, _laptop.Read($"batch/f{i:00}.txt"));
    }
}
