using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Core.Hashing;
using Pairnets.Server;
using Pairnets.Server.Cli;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Server storage behavior that needs control over time or crash states.</summary>
public class ServerStoreTests
{
    private static MemoryStream Body(string s) => new(Encoding.UTF8.GetBytes(s));

    private static string H(string s) => ContentHash.Of(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task PurgeRespectsRetentionAndMinimumVersions()
    {
        using var dir = new TempDir("purge");
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SyncStore(new ServerPaths(dir.Path), NullLogger<SyncStore>.Instance, clock);
        store.Initialize();

        string? prev = null;
        for (var i = 0; i < 10; i++)
        {
            var r = await store.PutAsync("f.txt", prev ?? "none", null, Body("v" + i), default);
            Assert.Equal(ChangeStatus.Ok, r.Status);
            prev = r.Entry!.Hash;
            clock.Advance(TimeSpan.FromDays(1));
        }
        // A deleted file whose only history is old.
        await store.PutAsync("gone.txt", "none", null, Body("g"), default);
        await store.DeleteAsync("gone.txt", H("g"));
        Assert.Equal(9, store.ListHistory("f.txt").Count);

        clock.Advance(TimeSpan.FromDays(100));
        var (deleted, _) = await store.PurgeHistoryAsync(TimeSpan.FromDays(30), 5, dryRun: false, default);
        Assert.Equal(4, deleted);
        Assert.Equal(5, store.ListHistory("f.txt").Count);
        Assert.Single(store.ListHistory("gone.txt"));

        // Nothing is expired within retention.
        Assert.Equal(0, (await store.PurgeHistoryAsync(TimeSpan.FromDays(1000), 0, dryRun: false, default)).Deleted);
        var dry = await store.PurgeHistoryAsync(TimeSpan.Zero, 0, dryRun: true, default);
        Assert.Equal(6, dry.Deleted);
        Assert.Equal(5, store.ListHistory("f.txt").Count);
    }

    [Fact]
    public async Task JournalRollsBackWhenTmpWasNotMoved()
    {
        using var dir = new TempDir("journal");
        var paths = new ServerPaths(dir.Path);
        using (var store = new SyncStore(paths, NullLogger<SyncStore>.Instance))
        {
            store.Initialize();
            await store.PutAsync("a.txt", "none", null, Body("old"), default);
        }
        // Simulate a crash after the old file moved to history but before the new one arrived.
        var manifest = new ManifestStore(paths.Database);
        File.WriteAllText(Path.Combine(paths.Tmp, "x.upload"), "new");
        var historyRel = Path.Combine("a.txt", "20260101T000000000Z-" + H("old")[..8]);
        Directory.CreateDirectory(Path.Combine(paths.History, "a.txt"));
        File.Move(Path.Combine(paths.Files, "a.txt"), Path.Combine(paths.History, historyRel));
        manifest.AddJournal("put", "a.txt", "x.upload", historyRel, H("new"), 3, 1);
        ManifestStore.ReleasePools();

        using (var store = new SyncStore(paths, NullLogger<SyncStore>.Instance))
        {
            store.Initialize();
            Assert.Equal("old", File.ReadAllText(Path.Combine(paths.Files, "a.txt")));
            Assert.Equal(H("old"), store.Manifest.Get("a.txt")!.Hash);
            Assert.Empty(store.Manifest.PendingJournal());
            Assert.Empty(Directory.EnumerateFiles(paths.Tmp));
            Assert.True(store.DetectDrift().IsClean);
        }
        ManifestStore.ReleasePools();
    }

    [Fact]
    public async Task JournalRollsForwardWhenFileWasMoved()
    {
        using var dir = new TempDir("journal2");
        var paths = new ServerPaths(dir.Path);
        using (var store = new SyncStore(paths, NullLogger<SyncStore>.Instance))
            store.Initialize();
        var manifest = new ManifestStore(paths.Database);
        File.WriteAllText(Path.Combine(paths.Files, "n.txt"), "new");
        manifest.AddJournal("put", "n.txt", "gone.upload", null, H("new"), 3, 1700000000000);
        ManifestStore.ReleasePools();

        using (var store = new SyncStore(paths, NullLogger<SyncStore>.Instance))
        {
            store.Initialize();
            var e = store.Manifest.Get("n.txt")!;
            Assert.Equal(H("new"), e.Hash);
            Assert.False(e.Deleted);
            Assert.Empty(store.Manifest.PendingJournal());
            var r = await store.PutAsync("n.txt", "none", null, Body("x"), default);
            Assert.Equal(ChangeStatus.Conflict, r.Status);
        }
        ManifestStore.ReleasePools();
    }

    [Fact]
    public void DataDirIsLockedAgainstSecondProcess()
    {
        using var dir = new TempDir("lock");
        var paths = new ServerPaths(dir.Path);
        using var store = new SyncStore(paths, NullLogger<SyncStore>.Instance);
        store.Initialize();
        var ex = Assert.Throws<IOException>(() => SyncStore.AcquireDataDirLock(paths));
        Assert.Contains("systemctl stop", ex.Message);
        ManifestStore.ReleasePools();
    }

    [Fact]
    public async Task RescanAddsUnknownAndReportsMissing()
    {
        using var dir = new TempDir("rescan");
        var paths = new ServerPaths(dir.Path);
        using var store = new SyncStore(paths, NullLogger<SyncStore>.Instance);
        store.Initialize();
        await store.PutAsync("kept.txt", "none", null, Body("k"), default);
        await store.PutAsync("lost.txt", "none", null, Body("l"), default);
        File.Delete(Path.Combine(paths.Files, "lost.txt"));
        Directory.CreateDirectory(Path.Combine(paths.Files, "hand"));
        File.WriteAllText(Path.Combine(paths.Files, "hand", "added.txt"), "by hand");
        RawFiles.Write(Path.Combine(paths.Files, "bad."), "invalid");

        var drift = store.DetectDrift();
        Assert.Equal(["hand/added.txt"], drift.Unknown);
        Assert.Equal(["lost.txt"], drift.Missing);
        Assert.Equal(["bad."], drift.InvalidNames);

        var output = new StringWriter();
        await CliCommands.RescanAsync(store, dryRun: true, output);
        Assert.Null(store.Manifest.Get("hand/added.txt"));
        await CliCommands.RescanAsync(store, dryRun: false, output);
        Assert.Equal(H("by hand"), store.Manifest.Get("hand/added.txt")!.Hash);
        Assert.Contains("missing: lost.txt", output.ToString());
        Assert.Contains("invalid name, ignored: bad.", output.ToString());
        RawFiles.Delete(Path.Combine(paths.Files, "bad."));
        ManifestStore.ReleasePools();
    }

    [Fact]
    public async Task OverwritingAFileAddedByHandKeepsItInHistory()
    {
        using var dir = new TempDir("hand");
        var paths = new ServerPaths(dir.Path);
        using var store = new SyncStore(paths, NullLogger<SyncStore>.Instance);
        store.Initialize();
        File.WriteAllText(Path.Combine(paths.Files, "x.txt"), "manual");
        var r = await store.PutAsync("x.txt", "none", null, Body("uploaded"), default);
        Assert.Equal(ChangeStatus.Ok, r.Status);
        Assert.Single(store.ListHistory("x.txt"));
        ManifestStore.ReleasePools();
    }

    [Fact]
    public void ThrottleDelaysAfterRepeatedFailuresAndResets()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var t = new FailureThrottle(clock);
        var ip = IPAddress.Parse("192.0.2.1");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(TimeSpan.Zero, t.CurrentDelay(ip));
            t.RecordFailure(ip);
        }
        Assert.Equal(TimeSpan.FromSeconds(1), t.CurrentDelay(ip));
        for (var i = 0; i < 10; i++)
            t.RecordFailure(ip);
        Assert.Equal(TimeSpan.FromSeconds(10), t.CurrentDelay(ip));
        Assert.Equal(TimeSpan.Zero, t.CurrentDelay(IPAddress.Parse("192.0.2.2")));
        t.RecordSuccess(ip);
        Assert.Equal(TimeSpan.Zero, t.CurrentDelay(ip));
        t.RecordFailure(ip);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.Zero, t.CurrentDelay(ip));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("fifteen-chars!!")]
    public void ServerRefusesToStartWithoutAStrongToken(string? token)
    {
        using var dir = new TempDir("tok");
        var ex = Assert.Throws<InvalidOperationException>(() => PairnetsServerHost.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:Token"] = token, ["Sync:DataDir"] = dir.Path });
        }));
        Assert.Contains("openssl rand -hex 32", ex.Message);
    }

    [Fact]
    public void OptionsAcceptSyncTokenShortcut()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SYNC_TOKEN"] = "  0123456789abcdef  " }).Build();
        var options = SyncOptions.FromConfiguration(config);
        Assert.Equal("0123456789abcdef", options.Token);
        Assert.Null(options.ValidateForServe());
    }

    [Theory]
    [InlineData("PAIRNETS_PUBLIC_URL")] // what install.sh writes into /etc/pairnets/pairnets.env
    [InlineData("PUBLIC_URL")]
    [InlineData("Sync:PublicUrl")]
    public void OptionsReadThePublicUrlFromEveryNameItGoesBy(string key)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "https://nest.example.com/" }).Build();

        Assert.Equal("https://nest.example.com", SyncOptions.FromConfiguration(config).PublicUrl); // trailing slash dropped
    }
}
