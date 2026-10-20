using System.Text;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>The Files page's logic against the real server: listing, opening, saving back, adding, moving, deleting.</summary>
public class NestFilesTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private Device _other = null!;
    private readonly TempDir _folder = new("files");
    private readonly TempDir _stateBase = new("files-state");
    private readonly TempDir _elsewhere = new("files-elsewhere");

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _other = new Device("desktop", _server);
    }

    public async Task DisposeAsync()
    {
        await _other.DisposeAsync();
        await _server.DisposeAsync();
        _folder.Dispose();
        _stateBase.Dispose();
        _elsewhere.Dispose();
    }

    private ClientSession Start(params (string Folder, FilePlacement Placement)[] placement)
    {
        var settings = new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused-by-session",
            Folder = _folder.Path,
            DeviceName = "laptop",
            FirstRunCompleted = true,
        };
        foreach (var (folder, p) in placement)
            settings.Placement[folder] = p;
        var session = ClientSession.Start(settings, _server.Token, new PermanentDeleteTrash(), stateBaseDir: _stateBase.Path, runnerOptions: o => new RunnerOptions
        {
            ServerUrl = o.ServerUrl,
            Token = o.Token,
            DeviceId = o.DeviceId,
            WatcherDebounce = TimeSpan.FromMilliseconds(200),
            PeriodicInterval = TimeSpan.FromHours(1),
            UnstableRetry = TimeSpan.FromMilliseconds(300),
            ErrorRetry = TimeSpan.FromMilliseconds(500),
            OfflineBackoff = [TimeSpan.FromMilliseconds(200)],
        });
        session.Files.Opened.QuietFor = TimeSpan.FromMilliseconds(300);
        return session;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    private async Task WaitIdle(ClientSession s) =>
        await WaitUntil(() => s.Status.Status == RunnerStatus.Idle && s.Status.LastSyncAt is not null, "the first sync");

    private string Here(string rel) => Path.Combine(_folder.Path, rel.Replace('/', Path.DirectorySeparatorChar));

    private string? OnNest(string path) =>
        _server.Store.ReadManifest(null).Entries.FirstOrDefault(e => e.Path == path && !e.Deleted) is { } e
            ? File.ReadAllText(Path.Combine(_server.Paths.Files, path.Replace('/', Path.DirectorySeparatorChar)))
            : null;

    private async Task OnTheNestAsync(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
            _other.Write(path, content);
        Assert.Equal(PassOutcome.Completed, (await _other.SyncAsync()).Outcome);
    }

    [Fact]
    public async Task ListsFoldersAndFilesWithWhereTheyAre()
    {
        await OnTheNestAsync(("Photos/beach.jpg", "beach"), ("Photos/2024/snow.jpg", "snow"), ("notes.txt", "notes"));
        await using var session = Start(("Photos", FilePlacement.NestOnly));
        await WaitIdle(session);

        var top = session.Files.List("");
        Assert.Equal(["Photos", "notes.txt"], top.Select(e => e.Name));
        var photos = top[0];
        Assert.True(photos.IsFolder);
        Assert.Equal(2, photos.Files);
        Assert.Equal("Only on the nest", photos.WhereText);
        Assert.Equal(FileWhere.Here, top[1].Where);

        var inside = session.Files.List("Photos");
        Assert.Equal(["2024", "beach.jpg"], inside.Select(e => e.Name));
        Assert.Equal(FileWhere.NestOnly, inside[1].Where);
        Assert.False(File.Exists(Here("Photos/beach.jpg")));
        Assert.Equal(["Photos"], session.Files.Folders());
        Assert.Equal(0, session.Files.BytesHere("Photos"));
        Assert.Equal(5, session.Files.BytesHere(""));
    }

    [Fact]
    public async Task OpeningANestOnlyFileGivesACopy_AndSavingItSendsTheChangeToTheNest()
    {
        await OnTheNestAsync(("Docs/letter.txt", "Dear nest"));
        await using var session = Start(("Docs", FilePlacement.NestOnly));
        await WaitIdle(session);

        var opened = await session.Files.OpenAsync("Docs/letter.txt");
        Assert.True(opened.Ok, opened.Problem);
        Assert.Equal("Dear nest", File.ReadAllText(opened.OpenPath!));
        Assert.DoesNotContain(_folder.Path, opened.OpenPath!); // not in the Pairnets folder

        File.WriteAllText(opened.OpenPath!, "Dear nest, edited");
        await WaitUntil(() => OnNest("Docs/letter.txt") == "Dear nest, edited", "the change on the nest");
        Assert.Single(_server.Store.ListHistory("Docs/letter.txt")); // the old version is kept
        Assert.False(File.Exists(Here("Docs/letter.txt")));

        // Opening it again, unchanged since, gives the same copy.
        Assert.Equal(opened.OpenPath, (await session.Files.OpenAsync("Docs/letter.txt")).OpenPath);
    }

    [Fact]
    public async Task ASaveAfterTheNestChangedKeepsBothVersions()
    {
        await OnTheNestAsync(("Docs/plan.txt", "v1"));
        await using var session = Start(("Docs", FilePlacement.NestOnly));
        await WaitIdle(session);
        var opened = await session.Files.OpenAsync("Docs/plan.txt");

        await OnTheNestAsync(("Docs/plan.txt", "v2 from the desktop"));
        File.WriteAllText(opened.OpenPath!, "v2 from the laptop");
        await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Any(e => !e.Deleted && e.Path.StartsWith("Docs/plan (", StringComparison.Ordinal)), "the conflict copy");
        Assert.Equal("v2 from the desktop", OnNest("Docs/plan.txt"));
        var copy = _server.Store.ReadManifest(null).Entries.Single(e => !e.Deleted && e.Path.StartsWith("Docs/plan (", StringComparison.Ordinal));
        Assert.Equal("v2 from the laptop", OnNest(copy.Path));
    }

    [Fact]
    public async Task AFileHereOpensWhereItIs()
    {
        await OnTheNestAsync(("notes.txt", "notes"));
        await using var session = Start();
        await WaitUntil(() => File.Exists(Here("notes.txt")), "the download");
        var opened = await session.Files.OpenAsync("notes.txt");
        Assert.Equal(Path.GetFullPath(Here("notes.txt")), Path.GetFullPath(opened.OpenPath!));
    }

    [Fact]
    public async Task AddingToANestOnlyFolderUploadsStraightThere()
    {
        await OnTheNestAsync(("Photos/old.jpg", "old"));
        await using var session = Start(("Photos", FilePlacement.NestOnly));
        await WaitIdle(session);
        var source = Path.Combine(_elsewhere.Path, "new.jpg");
        File.WriteAllText(source, "new picture");

        var added = await session.Files.AddAsync([source], "Photos");
        Assert.True(added.Ok, added.Problem);
        Assert.Equal("new picture", OnNest("Photos/new.jpg"));
        Assert.False(File.Exists(Here("Photos/new.jpg")));
        Assert.Contains(session.Files.List("Photos"), e => e.Name == "new.jpg");
        Assert.False((await session.Files.AddAsync([source], "Photos")).Ok); // already there
    }

    [Fact]
    public async Task AddingToAFolderOnThisPcCopiesItIntoThePairnetsFolder()
    {
        await using var session = Start();
        await WaitIdle(session);
        var source = Path.Combine(_elsewhere.Path, "report.txt");
        File.WriteAllText(source, "report");
        Assert.True((await session.Files.AddAsync([source], "Work")).Ok);
        Assert.Equal("report", File.ReadAllText(Here("Work/report.txt")));
        await WaitUntil(() => OnNest("Work/report.txt") == "report", "the upload by the sync");
    }

    [Fact]
    public async Task RenamingAndMovingFollowOnTheNestAndHere()
    {
        await OnTheNestAsync(("Album/a.jpg", "a"), ("Album/b.jpg", "b"), ("keep.txt", "k"));
        await using var session = Start(("Album", FilePlacement.NestOnly));
        await WaitIdle(session);

        Assert.True((await session.Files.RenameAsync("Album/a.jpg", "A.jpg")).Ok); // only the letter case
        Assert.Equal("a", OnNest("Album/A.jpg"));
        Assert.True((await session.Files.MoveAsync("Album", "Archive")).Ok); // a whole folder
        Assert.Equal("a", OnNest("Archive/Album/A.jpg"));
        Assert.Equal("b", OnNest("Archive/Album/b.jpg"));
        Assert.Null(OnNest("Album/b.jpg"));

        Assert.True((await session.Files.RenameAsync("keep.txt", "kept.txt")).Ok); // a file here moves along
        Assert.Equal("k", File.ReadAllText(Here("kept.txt")));
        Assert.False(File.Exists(Here("keep.txt")));
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "idle");
        Assert.Equal("k", OnNest("kept.txt"));
        Assert.Null(OnNest("keep.txt"));
        Assert.False((await session.Files.RenameAsync("kept.txt", "a/b")).Ok);
    }

    [Fact]
    public async Task DeletingRemovesItOnTheNestAndHere_AndHistoryKeepsIt()
    {
        await OnTheNestAsync(("Old/x.txt", "x"), ("Old/y.txt", "y"), ("here.txt", "h"));
        await using var session = Start(("Old", FilePlacement.NestOnly));
        await WaitUntil(() => File.Exists(Here("here.txt")), "the download");

        Assert.True((await session.Files.DeleteAsync("Old")).Ok);
        Assert.Null(OnNest("Old/x.txt"));
        Assert.Single(_server.Store.ListHistory("Old/y.txt"));
        Assert.True((await session.Files.DeleteAsync("here.txt")).Ok);
        Assert.False(File.Exists(Here("here.txt")));
        Assert.Empty(session.Files.List(""));
    }

    [Fact]
    public async Task WhileTheNestIsFullOnlyOpeningWorks()
    {
        await OnTheNestAsync(("Docs/a.txt", "a"));
        await using var session = Start(("Docs", FilePlacement.NestOnly));
        await WaitIdle(session);
        session.UpdateStatusForTests(s => s with { Server = new ServerInfo("id", 1, 1, DiskFreeBytes: 1024, DiskTotalBytes: 1L << 40) });
        Assert.True(session.Files.NestFull);

        var source = Path.Combine(_elsewhere.Path, "b.txt");
        File.WriteAllText(source, "b");
        foreach (var refused in new[]
        {
            await session.Files.AddAsync([source], "Docs"),
            await session.Files.RenameAsync("Docs/a.txt", "c.txt"),
            await session.Files.MoveAsync("Docs/a.txt", ""),
            await session.Files.DeleteAsync("Docs/a.txt"),
        })
        {
            Assert.False(refused.Ok);
            Assert.True(refused.NestFull);
            Assert.Equal(NestFiles.FullMessage, refused.Problem);
        }
        var opened = await session.Files.OpenAsync("Docs/a.txt");
        Assert.True(opened.Ok);

        // A change to an opened copy waits until the nest has room, and is kept meanwhile.
        var waited = new TaskCompletionSource<string>();
        session.Files.Opened.NestFullWhileSaving += p => waited.TrySetResult(p);
        File.WriteAllText(opened.OpenPath!, "a, changed");
        Assert.Equal("Docs/a.txt", await waited.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("a", OnNest("Docs/a.txt"));
        session.UpdateStatusForTests(s => s with { Server = null });
        await session.Files.Opened.SweepAsync();
        Assert.Equal("a, changed", OnNest("Docs/a.txt"));
    }

    [Fact]
    public async Task ChoosingWhereAFolderLivesAsksTheAppToSaveAndRestart()
    {
        await using var session = Start(("Photos", FilePlacement.NestOnly));
        ClientSettings? saved = null;
        session.AccountChanged += (next, _) => saved = next;

        session.Files.SetPlacement("Photos/Keep", FilePlacement.OnThisPc);
        Assert.Equal(FilePlacement.OnThisPc, saved!.Placement["Photos/Keep"]);
        Assert.Equal(FilePlacement.NestOnly, saved.Placement["Photos"]);
        Assert.False(session.Settings.Placement.ContainsKey("Photos/Keep")); // the running session's are left alone

        session.Files.SetPlacement("Photos", FilePlacement.OnThisPc); // the same as above it: no choice of its own
        Assert.False(saved.Placement.ContainsKey("Photos"));
    }
}
