using Pairnets.Core;
using Pairnets.Core.Paths;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Engine behavior against an in-memory server. The real server is covered by integration tests.</summary>
public class SyncEngineUnitTests : IDisposable
{
    private readonly TempDir _folder = new("eng");
    private readonly TempDir _stateDir = new("engstate");
    private readonly StateDb _state;
    private readonly FakeApi _api = new();
    private readonly SyncEngine _engine;

    public SyncEngineUnitTests()
    {
        _state = new StateDb(_stateDir.Combine("state.db"));
        _engine = new SyncEngine(new EngineOptions { Folder = _folder.Path, DeviceName = "PC", StabilityWindow = TimeSpan.Zero }, _api, _state);
    }

    private string Local(string rel) => Path.Combine(_folder.Path, PathRules.ToOsRelative(rel));

    private void Write(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Local(rel))!);
        File.WriteAllText(Local(rel), content);
    }

    private Task<PassResult> Pass(bool full = false) => _engine.RunPassAsync(new PassOptions("test", full), CancellationToken.None);

    [Fact]
    public async Task FirstSyncCreatesMarkerAndMergesWithoutDeleting()
    {
        Write("local-only.txt", "L");
        Write("same.txt", "S");
        _api.Put("same.txt", "S");
        _api.Put("server-only.txt", "R");

        var r = await Pass();
        Assert.Equal(PassOutcome.Completed, r.Outcome);
        Assert.Equal(1, r.Uploaded);
        Assert.Equal(1, r.Downloaded);
        Assert.Equal(1, r.Recorded);
        Assert.Equal(0, r.DeletedLocal + r.DeletedRemote);
        Assert.True(File.Exists(_engine.MarkerPath));
        Assert.Equal(_state.MarkerId, _engine.ReadMarkerFile());
        Assert.Equal("R", File.ReadAllText(Local("server-only.txt")));
        Assert.Equal("L", _api.Content("local-only.txt"));

        var again = await Pass();
        Assert.Equal(0, again.Changes);
    }

    [Fact]
    public async Task ConflictKeepsBothVersions()
    {
        Write("doc.txt", "v1");
        await Pass();
        Write("doc.txt", "local edit");
        _api.Put("doc.txt", "server edit");

        var r = await Pass();
        Assert.Equal(1, r.Conflicts);
        var copy = Assert.Single(r.ConflictCopies).ConflictCopyPath;
        Assert.Matches(@"^doc \(conflict PC \d{4}-\d{2}-\d{2} \d{6}\)\.txt$", copy);
        Assert.Equal("server edit", File.ReadAllText(Local("doc.txt")));
        Assert.Equal("local edit", File.ReadAllText(Local(copy)));
        Assert.Equal("local edit", _api.Content(copy));
        Assert.Equal("server edit", _api.Content("doc.txt"));
    }

    [Fact]
    public async Task MarkerMissingBlocksEverything()
    {
        Write("a.txt", "a");
        await Pass();
        File.Delete(_engine.MarkerPath);
        File.Delete(Local("a.txt"));

        var r = await Pass();
        Assert.Equal(PassOutcome.Blocked, r.Outcome);
        Assert.Equal(BlockReason.MarkerMissing, r.BlockReason);
        Assert.Equal("a", _api.Content("a.txt"));
    }

    [Fact]
    public async Task ThePlannedUploadsAndDownloadsAreToldInRunOrder()
    {
        IReadOnlyList<(string Path, string Operation)>? planned = null;
        _engine.ExecutionPlanned += p => planned = p;
        Write("b.txt", "b");
        Write("a/c.txt", "c");
        Write("d.txt", "d");
        Write("e.txt", "e");
        _api.Put("z.txt", "z");
        _api.Put("m.txt", "m");

        await Pass();
        Assert.Equal([("a/c.txt", "upload"), ("b.txt", "upload"), ("d.txt", "upload"), ("e.txt", "upload"), ("m.txt", "download"), ("z.txt", "download")], planned);

        // Deletions and conflicts are left out.
        File.Delete(Local("d.txt"));
        _api.Delete("e.txt");
        Write("a/c.txt", "local edit");
        _api.Put("a/c.txt", "server edit");
        Write("n.txt", "n");
        _api.Put("y.txt", "y");
        var r = await Pass();
        Assert.Equal((1, 1, 1), (r.Conflicts, r.DeletedLocal, r.DeletedRemote));
        Assert.Equal([("n.txt", "upload"), ("y.txt", "download")], planned);
    }

    [Fact]
    public async Task OfflineIsReportedAndRecovers()
    {
        Write("a.txt", "a");
        _api.Offline = true;
        var r = await Pass();
        Assert.Equal(PassOutcome.Offline, r.Outcome);
        _api.Offline = false;
        r = await Pass();
        Assert.Equal(1, r.Uploaded);
    }

    [Fact]
    public async Task MassDeleteGuardAsksOnlyFromOneHundredDeletions()
    {
        for (var i = 0; i < 250; i++)
            Write($"f{i:000}.txt", i.ToString());
        await Pass();

        // 99 deletions are just done...
        for (var i = 0; i < 99; i++)
            File.Delete(Local($"f{i:000}.txt"));
        var r = await Pass();
        Assert.Equal(PassOutcome.Completed, r.Outcome);
        Assert.Equal(99, r.DeletedRemote);

        // ...100 in one pass are blocked until allowed.
        for (var i = 99; i < 199; i++)
            File.Delete(Local($"f{i:000}.txt"));
        r = await Pass();
        Assert.Equal(PassOutcome.Blocked, r.Outcome);
        Assert.Equal(BlockReason.MassDelete, r.BlockReason);
        Assert.Equal(100, r.BlockedDeletes.Count);
        Assert.NotNull(_api.Content("f099.txt"));

        Assert.Equal(100, _engine.ApproveBlockedDeletions());
        r = await Pass();
        Assert.Equal(100, r.DeletedRemote);
        Assert.Null(_api.Content("f099.txt"));
    }

    [Theory]
    [InlineData(BigDeleteModes.AskEverywhere, true, true)]
    [InlineData(BigDeleteModes.AskWhereDeleted, false, true)]
    [InlineData(BigDeleteModes.Never, false, false)]
    [InlineData(null, true, true)] // an older nest: everywhere asks, as before
    public async Task BeforeBigDeletesDecidesWhoAsks(string? mode, bool asksForDeletesFromTheNest, bool asksForDeletesMadeHere)
    {
        _engine.BigDeletes = () => mode;
        BigDeleteBatch? noted = null;
        _engine.BigDeleteCarriedOut += b => noted = b;
        for (var i = 0; i < 400; i++)
            Write($"f{i:000}.txt", i.ToString());
        await Pass();

        // 150 deleted on another computer (the nest marks them deleted): this computer follows, or asks.
        for (var i = 0; i < 150; i++)
            _api.Delete($"f{i:000}.txt");
        var r = await Pass();
        Assert.Equal(asksForDeletesFromTheNest, r.Outcome == PassOutcome.Blocked);
        Assert.Equal(asksForDeletesFromTheNest, File.Exists(Local("f000.txt")));
        if (!asksForDeletesFromTheNest)
        {
            Assert.Equal(150, r.DeletedLocal);
            Assert.Equal((150, false), (noted!.Count, noted.FromHere));
            Assert.Equal(150, BigDeleteBatch.Load(_engine.State)!.Count); // kept for "Put them all back"
        }
        else
        {
            Assert.Equal(150, _engine.ApproveBlockedDeletions());
            Assert.Equal(150, (await Pass()).DeletedLocal);
        }

        // 150 deleted here: asks unless the nest says never.
        noted = null;
        for (var i = 150; i < 300; i++)
            File.Delete(Local($"f{i:000}.txt"));
        r = await Pass();
        Assert.Equal(asksForDeletesMadeHere, r.Outcome == PassOutcome.Blocked);
        Assert.Equal(asksForDeletesMadeHere, _api.Content("f150.txt") is not null);
        if (asksForDeletesMadeHere)
        {
            _engine.ApproveBlockedDeletions();
            r = await Pass();
        }
        Assert.Equal(150, r.DeletedRemote);
        Assert.Equal((150, true), (noted!.Count, noted.FromHere));

        // Emptying the whole folder asks whatever the nest says.
        for (var i = 300; i < 400; i++)
            File.Delete(Local($"f{i:000}.txt"));
        r = await Pass();
        Assert.Equal(BlockReason.FolderEmpty, r.BlockReason);
        Assert.NotNull(_api.Content("f399.txt"));
    }

    [Fact]
    public async Task SmallNestLosesMostFilesWithoutAsking()
    {
        for (var i = 0; i < 10; i++)
            Write($"f{i}.txt", i.ToString());
        await Pass();

        for (var i = 0; i < 9; i++)
            File.Delete(Local($"f{i}.txt"));
        var r = await Pass();
        Assert.Equal(PassOutcome.Completed, r.Outcome);
        Assert.Equal(9, r.DeletedRemote);

        // Emptying the whole folder still asks: that is usually a wrong or unplugged drive.
        File.Delete(Local("f9.txt"));
        r = await Pass();
        Assert.Equal(BlockReason.FolderEmpty, r.BlockReason);
        Assert.NotNull(_api.Content("f9.txt"));
    }

    [Fact]
    public async Task ServerRollbackIsDetected()
    {
        Write("a.txt", "a");
        await Pass();
        await Pass(); // the cursor now includes our own upload
        _api.Version = 0;
        var r = await Pass();
        Assert.Equal(BlockReason.ServerRolledBack, r.BlockReason);

        _engine.RelinkToServer();
        r = await Pass();
        Assert.Equal(PassOutcome.Completed, r.Outcome);
    }

    [Fact]
    public async Task ServerReplacementIsDetected()
    {
        Write("a.txt", "a");
        await Pass();
        _api.ServerId = "another-server";
        var r = await Pass();
        Assert.Equal(BlockReason.ServerChanged, r.BlockReason);
    }

    public void Dispose()
    {
        _state.Dispose();
        _folder.Dispose();
        _stateDir.Dispose();
    }
}
