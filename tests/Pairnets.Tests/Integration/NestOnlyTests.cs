using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Folders whose files live on the nest only, with two computers and the real in-process nest.</summary>
public class NestOnlyTests : IAsyncLifetime
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

    private static PassResult Completed(PassResult r)
    {
        Assert.True(r.Outcome == PassOutcome.Completed, r.ToString());
        return r;
    }

    private async Task<Dictionary<string, string>> NestFilesAsync()
    {
        var manifest = await _desktop.Api.GetManifestAsync(null, CancellationToken.None);
        return manifest.Entries.Where(e => !e.Deleted).ToDictionary(e => e.Path, e => e.Hash!, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SwitchingAFolderToTheNestFreesItsSyncedFilesOnly()
    {
        _laptop.Write("Photos/beach.jpg", "beach");
        _laptop.Write("Photos/2024/snow.jpg", "snow");
        _laptop.Write("Documents/cv.txt", "cv");
        Completed(await _laptop.SyncAsync());

        _laptop.Place(("Photos", FilePlacement.NestOnly));
        _laptop.Write("Photos/new.jpg", "not on the nest yet");
        var pass = Completed(await _laptop.SyncAsync());

        // The new one is uploaded first; the synced ones are freed; other folders stay.
        Assert.Equal(1, pass.Uploaded);
        Assert.Equal(2, pass.Freed);
        Assert.Equal(0, pass.DeletedRemote);
        Assert.Equal(["Documents/cv.txt", "Photos/new.jpg"], _laptop.Files());
        var nest = await NestFilesAsync();
        Assert.Equal(4, nest.Count);

        // The new one goes on the next pass, once the nest has it.
        Assert.Equal(1, Completed(await _laptop.SyncAsync()).Freed);
        Assert.Equal(["Documents/cv.txt"], _laptop.Files());
        Assert.Equal(4, (await NestFilesAsync()).Count);
        Assert.False(Directory.Exists(_laptop.Full("Photos/2024")));
    }

    [Fact]
    public async Task NewFilesFromAnotherComputerStayOnTheNest()
    {
        _laptop.Place(("Photos", FilePlacement.NestOnly));
        Completed(await _laptop.SyncAsync());

        _desktop.Write("Photos/party.jpg", "party");
        _desktop.Write("Notes/todo.txt", "todo");
        Completed(await _desktop.SyncAsync());

        var pass = Completed(await _laptop.SyncUntilQuietAsync());
        Assert.Equal(["Notes/todo.txt"], _laptop.Files());
        Assert.Equal(0, pass.Downloaded);
        Assert.True((await NestFilesAsync()).ContainsKey("Photos/party.jpg"));
    }

    [Fact]
    public async Task ADeletionOnTheOtherComputerLeavesThisOneAlone()
    {
        _desktop.Write("Photos/a.jpg", "a");
        for (var i = 0; i < 9; i++)
            _desktop.Write($"other{i}.txt", "keeps one deletion under the desktop's mass-delete guard");
        Completed(await _desktop.SyncAsync());
        _laptop.Place(("Photos", FilePlacement.NestOnly));
        Completed(await _laptop.SyncUntilQuietAsync());

        _desktop.Delete("Photos/a.jpg");
        Completed(await _desktop.SyncAsync());
        var pass = Completed(await _laptop.SyncUntilQuietAsync());
        Assert.Equal(0, pass.DeletedLocal + pass.DeletedRemote);
        Assert.False((await NestFilesAsync()).ContainsKey("Photos/a.jpg"));
    }

    [Fact]
    public async Task FreeingNeverDeletesOnTheNestAndNeverTripsTheGuards()
    {
        // More files than the mass-delete guard allows, all freed at once, and the folder ends up empty.
        for (var i = 0; i < 80; i++)
            _laptop.Write($"f{i:00}.txt", "content " + i);
        Completed(await _laptop.SyncAsync());

        _laptop.Place(("", FilePlacement.NestOnly));
        var pass = Completed(await _laptop.SyncAsync());
        Assert.Equal(80, pass.Freed);
        Assert.Empty(_laptop.Files());
        Assert.Equal(80, (await NestFilesAsync()).Count);

        var again = Completed(await _laptop.SyncAsync());
        Assert.Equal(0, again.Changes);
        Assert.Equal(PassOutcome.Completed, again.Outcome);
        Assert.Equal(80, (await NestFilesAsync()).Count);
    }

    [Fact]
    public async Task SwitchingBackBringsEveryFileHome()
    {
        _desktop.Write("Photos/a.jpg", "a");
        _desktop.Write("Photos/b/c.jpg", "c");
        Completed(await _desktop.SyncAsync());
        _laptop.Place(("Photos", FilePlacement.NestOnly));
        Completed(await _laptop.SyncUntilQuietAsync());
        Assert.Empty(_laptop.Files());

        _laptop.Place();
        Completed(await _laptop.SyncUntilQuietAsync());
        Assert.Equal(_desktop.Snapshot(), _laptop.Snapshot());
    }

    [Fact]
    public async Task EditsHereStillReachTheNestBeforeTheCopyGoes()
    {
        _laptop.Write("Photos/a.txt", "v1");
        Completed(await _laptop.SyncAsync());
        _laptop.Write("Photos/a.txt", "v2, not synced yet");
        _laptop.Place(("Photos", FilePlacement.NestOnly));

        Completed(await _laptop.SyncUntilQuietAsync());
        Assert.Empty(_laptop.Files());
        Completed(await _desktop.SyncAsync());
        Assert.Equal("v2, not synced yet", _desktop.Read("Photos/a.txt"));
    }

    [Fact]
    public async Task ANewerVersionOnTheNestReplacesNothingHere()
    {
        _desktop.Write("Photos/a.txt", "v1");
        Completed(await _desktop.SyncAsync());
        Completed(await _laptop.SyncAsync());
        _desktop.Write("Photos/a.txt", "v2");
        Completed(await _desktop.SyncAsync());

        _laptop.Place(("Photos", FilePlacement.NestOnly));
        var pass = Completed(await _laptop.SyncUntilQuietAsync());
        Assert.Equal(0, pass.Downloaded);
        Assert.Empty(_laptop.Files());
        Assert.Equal(Core.Hashing.ContentHash.Of("v2"u8), (await NestFilesAsync())["Photos/a.txt"]);
    }

    [Fact]
    public async Task WhileTheNestIsFullNothingIsFreed()
    {
        _laptop.Write("Photos/a.jpg", "a");
        Completed(await _laptop.SyncAsync());
        _laptop.NestFull = true;
        _laptop.Place(("Photos", FilePlacement.NestOnly));

        var pass = Completed(await _laptop.SyncAsync());
        Assert.Equal(1, pass.KeptForRoom);
        Assert.Equal(["Photos/a.jpg"], _laptop.Files());

        _laptop.NestFull = false;
        Assert.Equal(1, Completed(await _laptop.SyncAsync()).Freed);
        Assert.Empty(_laptop.Files());
    }

    [Fact]
    public async Task AFileOpenInAnotherProgramIsKeptForNow()
    {
        _laptop.Write("Photos/open.jpg", "open");
        Completed(await _laptop.SyncAsync());
        _laptop.Place(("Photos", FilePlacement.NestOnly));

        if (OperatingSystem.IsWindows())
        {
            // Another program writing it: it must not disappear under it.
            using (new FileStream(_laptop.Full("Photos/open.jpg"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var held = Completed(await _laptop.SyncAsync());
                Assert.Equal(0, held.Freed);
                Assert.True(_laptop.Exists("Photos/open.jpg"));
            }
        }
        Assert.Equal(1, Completed(await _laptop.SyncAsync()).Freed);
    }

    [Fact]
    public async Task AStopHalfWayIsRepairedByTheNextPass()
    {
        _laptop.Write("Photos/a.jpg", "a");
        Completed(await _laptop.SyncAsync());

        // As if the app stopped right after forgetting the file's notes, before removing it.
        _laptop.State.RemoveFile("Photos/a.jpg");
        _laptop.Place(("Photos", FilePlacement.NestOnly));
        var pass = Completed(await _laptop.SyncAsync());
        Assert.Equal(0, pass.DeletedRemote);
        Assert.Equal(1, pass.Recorded);
        Assert.Equal(1, Completed(await _laptop.SyncAsync()).Freed);
        Assert.True((await NestFilesAsync()).ContainsKey("Photos/a.jpg"));
    }
}
