using System.Net;
using System.Text;
using Tether.Core.Client;
using Tether.Core.Settings;
using Tether.Core.Sync;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Integration;

/// <summary>The session object both desktop apps use, against the real server.</summary>
public class ClientSessionTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private readonly TempDir _folder = new("session");
    private readonly TempDir _stateBase = new("session-state");

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _folder.Dispose();
        _stateBase.Dispose();
    }

    private ClientSession Start(EngineHooks? hooks = null) => ClientSession.Start(new ClientSettings
    {
        ServerUrl = _server.Url.ToString(),
        ProtectedToken = "unused-by-session",
        Folder = _folder.Path,
        DeviceName = "mac",
        FirstRunCompleted = true,
    }, _server.Token, new PermanentDeleteTrash(), stateBaseDir: _stateBase.Path, runnerOptions: o => new RunnerOptions
    {
        ServerUrl = o.ServerUrl,
        Token = o.Token,
        DeviceId = o.DeviceId,
        WatcherDebounce = TimeSpan.FromMilliseconds(200),
        PeriodicInterval = TimeSpan.FromHours(1),
        UnstableRetry = TimeSpan.FromMilliseconds(300),
        OfflineBackoff = [TimeSpan.FromMilliseconds(200)],
    }, hooks: hooks);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task SessionSyncsAndReportsActivityAndStatus()
    {
        File.WriteAllText(Path.Combine(_folder.Path, "hello.txt"), "hi");
        await using var session = Start();
        var statuses = new List<StatusSnapshot>();
        session.StatusChanged += s => { lock (statuses) statuses.Add(s); };

        await WaitUntil(() => session.Activity.Items.Any(i => i.Kind == ActivityKind.Uploaded && i.Path == "hello.txt"), "upload activity");
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle && session.Status.LastSyncAt is not null, "idle status");
        Assert.Equal("Up to date", session.Status.Headline);
        Assert.StartsWith("Last synced", session.Status.LastSyncText);
        Assert.Null(session.Status.FixLabel);
        Assert.Equal("↑", session.Activity.Items.First(i => i.Kind == ActivityKind.Uploaded).Symbol);

        session.Pause();
        Assert.True(session.Settings.Paused);
        await WaitUntil(() => session.Status.Status == RunnerStatus.Paused, "paused");
        session.Resume();
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "resumed");
    }

    [Fact]
    public async Task BlockedDeletionsAreDescribedAndCanBeApproved()
    {
        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_folder.Path, $"f{i}.txt"), i.ToString());
        await using (var first = Start())
            await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Count == 10 && first.Status.Status == RunnerStatus.Idle, "first sync");

        for (var i = 0; i < 5; i++)
            File.Delete(Path.Combine(_folder.Path, $"f{i}.txt"));
        await using var session = Start();
        await WaitUntil(() => session.Status.Status == RunnerStatus.Blocked, "blocked");
        Assert.Equal(BlockReason.MassDelete, session.Status.BlockReason);
        Assert.Equal("Allow these deletions (5)…", session.Status.FixLabel);
        Assert.Contains("server: f0.txt", session.DescribePendingDeletes());
        Assert.Contains(session.Activity.Items, i => i.Kind == ActivityKind.Blocked);

        Assert.Equal(5, session.ApproveDeletions());
        await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Count(e => e.Deleted) == 5, "deletions");
    }

    [Fact]
    public async Task MovedFolderCheckUsesTheMarker()
    {
        await using var session = Start();
        await WaitUntil(() => session.Status.LastSyncAt is not null, "first sync");
        using var other = new TempDir("other");
        Assert.NotNull(session.CheckMovedFolder(other.Path));
        File.Copy(Path.Combine(_folder.Path, ".tether-marker"), Path.Combine(other.Path, ".tether-marker"));
        Assert.Null(session.CheckMovedFolder(other.Path));
    }

    [Fact]
    public async Task UnicodeNormalizationVariantsAreNameCollisions()
    {
        var nfc = "café.txt";          // é as one character
        var nfd = "café.txt";         // e + combining accent: the same name on macOS
        using var http = _server.RawHttp(_server.Token);
        var first = await http.PutAsync($"api/file?path={Uri.EscapeDataString(nfc)}&base=none", new StringContent("a", Encoding.UTF8));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await http.PutAsync($"api/file?path={Uri.EscapeDataString(nfd)}&base=none", new StringContent("b", Encoding.UTF8));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("case-collision", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FileCountGrowsWhenFilesKeepArrivingDuringASync()
    {
        var hooks = new EngineHooks { BeforeTransfer = (_, _) => Task.Delay(100) };
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_folder.Path, $"first{i:00}.txt"), "a" + i);
        await using var session = Start(hooks);
        var maxTotal = 0;
        session.StatusChanged += s => { if (s.FilesTotal > Volatile.Read(ref maxTotal)) Volatile.Write(ref maxTotal, s.FilesTotal); };

        await WaitUntil(() => session.Status.FilesDone >= 4, "first uploads");
        Assert.Equal(20, session.Status.FilesTotal);
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(_folder.Path, $"later{i:00}.txt"), "b" + i);

        // The new files are counted while the first pass is still running...
        await WaitUntil(() => session.Status.FilesTotal >= 32, "total to include files that arrived");
        // ...and the next pass continues the same count instead of starting again at 1.
        await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Count == 32, "all uploads");
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "idle");
        Assert.Equal(32, Volatile.Read(ref maxTotal));
    }
}
