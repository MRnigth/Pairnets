using Tether.Core.Paths;
using Tether.Core.State;
using Tether.Core.Sync;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Unit;

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
    public async Task MassDeleteGuardUsesTheLiteralRule()
    {
        for (var i = 0; i < 10; i++)
            Write($"f{i}.txt", i.ToString());
        await Pass();

        // 10 tracked: limit is min(20% x 10, 50) = 2. Two deletions pass...
        File.Delete(Local("f0.txt"));
        File.Delete(Local("f1.txt"));
        var r = await Pass();
        Assert.Equal(2, r.DeletedRemote);

        // ...8 tracked now: limit 1.6, so two more deletions are blocked.
        File.Delete(Local("f2.txt"));
        File.Delete(Local("f3.txt"));
        r = await Pass();
        Assert.Equal(PassOutcome.Blocked, r.Outcome);
        Assert.Equal(BlockReason.MassDelete, r.BlockReason);
        Assert.Equal(2, r.BlockedDeletes.Count);
        Assert.NotNull(_api.Content("f2.txt"));

        Assert.Equal(2, _engine.ApproveBlockedDeletions());
        r = await Pass();
        Assert.Equal(2, r.DeletedRemote);
        Assert.Null(_api.Content("f2.txt"));
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
