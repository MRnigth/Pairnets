using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public class LocalScannerTests : IDisposable
{
    private readonly TempDir _root = new("scan");
    private readonly TempDir _stateDir = new("scanstate");
    private readonly StateDb _state;
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow.AddHours(1));

    public LocalScannerTests() => _state = new StateDb(_stateDir.Combine("state.db"));

    private LocalScanner Scanner(TimeSpan? stability = null) =>
        new(_root.Path, new IgnoreList(["*.bak"]), _state, _clock, NullLogger.Instance)
        {
            StabilityWindow = stability ?? TimeSpan.FromSeconds(2),
        };

    private string Write(string rel, string content)
    {
        var full = Path.Combine(_root.Path, PathRules.ToOsRelative(rel));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task FindsFilesAndSkipsIgnoredAndBookkeeping()
    {
        Write("a.txt", "a");
        Write("sub/deeper/b.txt", "b");
        Write("x.bak", "ignored");
        Write("Thumbs.db", "ignored");
        Write(".git/config", "ignored");
        Write(".pairnets-tmp/1.part", "ignored");
        Write(".pairnets-marker", "ignored");

        var result = await Scanner().ScanAsync(CancellationToken.None);
        Assert.Equal(["a.txt", "sub/deeper/b.txt"], result.Files.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Files.Values, f => Assert.True(f.Stable));
        Assert.Equal(ContentHash.Of("a"u8), result.Files["a.txt"].Hash);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public async Task AScanStoppedPartWayKeepsWhatItReadSoTheNextOneGoesOn()
    {
        for (var i = 0; i < 10; i++)
            Write($"d{i}/f.txt", "file " + i); // one folder each: the scan stops between folders
        using var pause = new CancellationTokenSource();
        var stopped = new LocalScanner(_root.Path, new IgnoreList(["*.bak"]), _state, _clock, NullLogger.Instance)
        {
            SaveBatch = 3,
            ReportEvery = TimeSpan.Zero,
            Progress = p =>
            {
                if (p.FilesChecked >= 5)
                    pause.Cancel(); // Pause, after five files
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.ScanAsync(pause.Token));
        var kept = _state.LoadFiles().Values.Count(f => f.Hash is not null);
        Assert.InRange(kept, 5, 9);

        ScanProgress? last = null;
        var again = new LocalScanner(_root.Path, new IgnoreList(["*.bak"]), _state, _clock, NullLogger.Instance)
        {
            ReportEvery = TimeSpan.Zero,
            Progress = p => last = p,
        };
        var result = await again.ScanAsync(CancellationToken.None);
        Assert.Equal(10, result.Files.Count);
        Assert.All(result.Files.Values, f => Assert.True(f.Stable));
        Assert.Equal(10, last!.FilesChecked);
        Assert.Equal(10 - kept, last.FilesRead); // only what the stopped scan had not read yet
    }

    [Fact]
    public async Task ProgressNamesTheFileBeingReadAndHowFar()
    {
        Write("small.txt", "s");
        var big = Path.Combine(_root.Path, "big.bin");
        await File.WriteAllBytesAsync(big, new byte[3 * 1024 * 1024 + 5]);
        File.SetLastWriteTimeUtc(big, DateTime.UtcNow.AddMinutes(-1));
        var seen = new List<ScanProgress>();
        var scanner = new LocalScanner(_root.Path, new IgnoreList([]), _state, _clock, NullLogger.Instance)
        {
            ReportEvery = TimeSpan.Zero,
            Progress = seen.Add,
        };

        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(new ScanProgress(0, 0, null, 0, 0), seen[0]);
        var reading = seen.Where(p => p.ReadingPath == "big.bin").ToList();
        Assert.True(reading.Count >= 3, "a big file reports while it is read");
        Assert.All(reading, p => Assert.Equal(3 * 1024 * 1024 + 5, p.ReadingSize));
        Assert.Equal(3 * 1024 * 1024 + 5, reading.Max(p => p.ReadingBytes));
        Assert.Equal((2, 2, (string?)null), (seen[^1].FilesChecked, seen[^1].FilesRead, seen[^1].ReadingPath));
    }

    [Fact]
    public async Task RecentlyModifiedFilesAreUnstable()
    {
        Write("fresh.txt", "x");
        _clock.Now = DateTimeOffset.UtcNow; // the file was written "just now"
        var result = await Scanner().ScanAsync(CancellationToken.None);
        Assert.False(result.Files["fresh.txt"].Stable);
        Assert.Equal(1, result.UnstableCount);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task FutureMtimesAreNotStuckForever()
    {
        var full = Write("future.txt", "x");
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddYears(5));
        _clock.Now = DateTimeOffset.UtcNow;
        var result = await Scanner().ScanAsync(CancellationToken.None);
        Assert.True(result.Files["future.txt"].Stable);
    }

    [Fact]
    public async Task LockedFilesAreUnstableNotMissing()
    {
        var full = Write("locked.txt", "x");
        await using var hold = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Scanner().ScanAsync(CancellationToken.None);
        Assert.True(result.Files.ContainsKey("locked.txt"));
        Assert.False(result.Files["locked.txt"].Stable);
    }

    [Fact]
    public async Task UsesCacheButNotForRacyEntries()
    {
        var full = Write("c.txt", "one");
        var mtime = File.GetLastWriteTimeUtc(full).Ticks;
        // Pretend we hashed long after the last write, with a fake (wrong) hash: it must be reused.
        _state.SetCache("c.txt", 3, mtime, "cached-hash", mtime + TimeSpan.FromMinutes(1).Ticks);
        var result = await Scanner().ScanAsync(CancellationToken.None);
        Assert.Equal("cached-hash", result.Files["c.txt"].Hash);

        // Hashed inside the racy window: must be recomputed.
        _state.SetCache("c.txt", 3, mtime, "cached-hash", mtime + TimeSpan.FromMilliseconds(100).Ticks);
        result = await Scanner().ScanAsync(CancellationToken.None);
        Assert.Equal(ContentHash.Of("one"u8), result.Files["c.txt"].Hash);
    }

    [Fact]
    public async Task ReportsInvalidNames()
    {
        // Win32 silently strips a trailing dot; tools using \\?\ paths (or a Linux share) can still
        // create such names, which is exactly what the scanner must refuse to sync.
        var bad = Path.Combine(_root.Path, "trailing.");
        RawFiles.Write(bad, "x");
        Write("ok.txt", "x");
        try
        {
            var result = await Scanner().ScanAsync(CancellationToken.None);
            Assert.Equal(["ok.txt"], result.Files.Keys);
            Assert.Contains(result.InvalidNames, i => i.Path == "trailing." && i.Problem == PathProblem.TrailingDot);
        }
        finally
        {
            RawFiles.Delete(bad);
        }
    }

    [Fact]
    public async Task NeverFollowsSymlinks()
    {
        using var outside = new TempDir("outside");
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "s");
        Links.Folder(Path.Combine(_root.Path, "linkdir"), outside.Path);
        Links.TryFile(Path.Combine(_root.Path, "linkfile.txt"), Path.Combine(outside.Path, "secret.txt"));
        Write("real.txt", "r");

        var result = await Scanner().ScanAsync(CancellationToken.None);
        Assert.Equal(["real.txt"], result.Files.Keys);
    }

    [Fact]
    public async Task UnknownPrefixCoversChildren()
    {
        var result = new ScanResult();
        result.UnknownPrefixes.Add("locked-dir");
        Assert.True(result.IsUnderUnknownPrefix("locked-dir/a.txt"));
        Assert.True(result.IsUnderUnknownPrefix("locked-dir"));
        Assert.False(result.IsUnderUnknownPrefix("locked-dir2/a.txt"));
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        _stateDir.Dispose();
    }
}
