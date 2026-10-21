using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Paths;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// The sync engine on a nest a friend shared with this computer (friend invites, 1.0.61.2), against an in-memory nest:
/// "View only" sends nothing and holds changes made here back until the person picks "Put back" or "Keep mine here";
/// with chosen folders, files outside them stay this computer's own; a 403 from the nest is never an error.
/// </summary>
public class SharedNestEngineTests : IDisposable
{
    private readonly TempDir _folder = new("shared");
    private readonly TempDir _stateDir = new("sharedstate");
    private readonly StateDb _state;
    private readonly FakeApi _nest = new();
    private readonly SharedApi _api;
    private readonly SyncEngine _engine;

    public SharedNestEngineTests()
    {
        _state = new StateDb(_stateDir.Combine("state.db"));
        _api = new SharedApi(_nest);
        _engine = new SyncEngine(new EngineOptions
        {
            Folder = _folder.Path,
            DeviceName = "ANNA-LAPTOP",
            StabilityWindow = TimeSpan.Zero,
            MassDeleteFraction = 1.0, // these nests hold two or three files: one deletion is not a mass delete here
            Access = NestAccess.Read("view", null, "Oskar Nilsson", "Home nest"),
        }, _api, _state);
    }

    private string Local(string rel) => Path.Combine(_folder.Path, PathRules.ToOsRelative(rel));

    private void Write(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Local(rel))!);
        File.WriteAllText(Local(rel), content);
        File.SetLastWriteTimeUtc(Local(rel), DateTime.UtcNow.AddSeconds(-10)); // settled, so the scan reads it at once
    }

    private Task<PassResult> Pass(bool full = false) => _engine.RunPassAsync(new PassOptions("test", full), CancellationToken.None);

    [Fact]
    public async Task ViewOnly_AnEditStaysHereAndIsNeverSent()
    {
        _nest.Put("Photos/trip.jpg", "nest v1");
        await Pass();
        Assert.Equal("nest v1", File.ReadAllText(Local("Photos/trip.jpg")));

        Write("Photos/trip.jpg", "mine");
        var r = await Pass();
        Assert.Equal(PassOutcome.Completed, r.Outcome);
        Assert.Equal(["Photos/trip.jpg"], r.HeldBack);
        Assert.Equal(0, r.Uploaded + r.Errors + r.Deferred);
        Assert.False(r.WantsRetry); // held back is not retried in a loop
        Assert.Equal("nest v1", _nest.Content("Photos/trip.jpg"));

        // The nest changes the same file: the change made here still stays.
        _nest.Put("Photos/trip.jpg", "nest v2");
        r = await Pass();
        Assert.Equal(["Photos/trip.jpg"], r.HeldBack);
        Assert.Equal(0, r.Conflicts + r.Downloaded);
        Assert.Equal("mine", File.ReadAllText(Local("Photos/trip.jpg")));
    }

    [Fact]
    public async Task ViewOnly_ChangesFromTheNestStillArrive()
    {
        _nest.Put("School/notes.docx", "v1");
        await Pass();
        _nest.Put("School/notes.docx", "v2");
        _nest.Put("School/new.docx", "new");
        var r = await Pass();
        Assert.Equal(2, r.Downloaded);
        Assert.Empty(r.HeldBack);
        Assert.Equal("v2", File.ReadAllText(Local("School/notes.docx")));

        _nest.Delete("School/new.docx");
        r = await Pass();
        Assert.Equal(1, r.DeletedLocal);
        Assert.False(File.Exists(Local("School/new.docx")));
    }

    [Fact]
    public async Task ViewOnly_PutBackBringsTheNestsVersionsBack()
    {
        _nest.Put("Photos/trip.jpg", "nest v1");
        _nest.Put("Photos/gone.jpg", "keep me");
        await Pass();
        Write("Photos/trip.jpg", "mine");
        File.Delete(Local("Photos/gone.jpg"));
        Write("Photos/made-here.jpg", "new here");
        var held = await Pass();
        Assert.Equal(["Photos/gone.jpg", "Photos/made-here.jpg", "Photos/trip.jpg"], held.HeldBack.Order(StringComparer.Ordinal));
        _nest.Put("Photos/trip.jpg", "nest v2");

        _engine.RequestPutBack();
        var r = await Pass();
        Assert.Empty(r.HeldBack);
        Assert.Equal(0, r.Errors);
        Assert.Equal("nest v2", File.ReadAllText(Local("Photos/trip.jpg")));
        Assert.Equal("keep me", File.ReadAllText(Local("Photos/gone.jpg")));
        Assert.False(File.Exists(Local("Photos/made-here.jpg")));
        Assert.Equal(["Photos/gone.jpg", "Photos/trip.jpg"], _nest.LivePaths()); // nothing was sent

        // Put back is used up: a new edit is held back again.
        Write("Photos/trip.jpg", "mine again");
        Assert.Equal(["Photos/trip.jpg"], (await Pass()).HeldBack);
    }

    [Fact]
    public async Task ViewOnly_KeepMineHereStopsAskingUntilTheFileChangesAgain()
    {
        _nest.Put("Photos/trip.jpg", "nest v1");
        await Pass();
        Write("Photos/trip.jpg", "mine");
        Assert.Single((await Pass()).HeldBack);

        Assert.Equal(1, _engine.KeepHeldBack());
        var r = await Pass();
        Assert.Empty(r.HeldBack); // kept: the card does not come back
        Assert.Equal("mine", File.ReadAllText(Local("Photos/trip.jpg")));
        Assert.Equal("nest v1", _nest.Content("Photos/trip.jpg"));

        Write("Photos/trip.jpg", "mine, edited again");
        Assert.Equal(["Photos/trip.jpg"], (await Pass()).HeldBack);
    }

    [Fact]
    public async Task ChosenFolders_FilesOutsideStayHereWithoutErrors()
    {
        _engine.Access = NestAccess.Read("edit", ["Photos"], "Oskar Nilsson", "Home nest");
        Write("Photos/b.jpg", "inside");
        Write("Notes/a.txt", "outside");
        Write("top.txt", "outside too");

        var r = await Pass();
        Assert.Equal(1, r.Uploaded);
        Assert.Equal(2, r.LocalOnly);
        Assert.Equal(0, r.Errors);
        Assert.Equal(["Photos/b.jpg"], _nest.LivePaths());

        // Again: still nothing to do, still no error.
        r = await Pass();
        Assert.Equal(0, r.Changes + r.Errors);
    }

    [Fact]
    public async Task ChosenFolders_AFolderThatLeavesTheShareStaysOnThisComputer()
    {
        _engine.Access = NestAccess.Read("edit", null, "Oskar Nilsson", "Home nest");
        _nest.Put("School/x.txt", "school");
        _nest.Put("Photos/y.jpg", "photo");
        await Pass();

        // The owner shares only Photos now: School drops out of what the nest lists.
        _engine.Access = NestAccess.Read("edit", ["Photos"], "Oskar Nilsson", "Home nest");
        _nest.Delete("School/x.txt");
        var r = await Pass();
        Assert.Equal(0, r.DeletedLocal);
        Assert.Equal("school", File.ReadAllText(Local("School/x.txt")));
        Assert.Null(_state.GetFile("School/x.txt")?.BaseHash); // no longer synced
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("view")]
    public async Task AShareMadeSmallerDuringAPassNeverDeletesTheFriendsFiles(string level)
    {
        _engine.Access = NestAccess.Read(level, ["Photos", "School"], "Oskar Nilsson", "Home nest");
        _api.Access = new AccessAnswer(level, ["Photos", "School"]);
        _nest.Put("School/x.txt", "school");
        _nest.Put("School/sub/y.txt", "deeper");
        _nest.Put("Photos/y.jpg", "photo");
        await Pass(full: true);
        Assert.Equal("school", File.ReadAllText(Local("School/x.txt")));

        // The owner takes School out after this pass asked what it may see, but before it reads the list: the nest
        // already leaves School out, while the app still thinks it is shared.
        _api.ListsOnly = ["Photos"];
        var r = await Pass(full: true);
        Assert.Equal(0, r.DeletedLocal);
        Assert.Equal(0, r.Errors);
        Assert.Equal("school", File.ReadAllText(Local("School/x.txt")));
        Assert.Equal("deeper", File.ReadAllText(Local("School/sub/y.txt")));
        Assert.Null(_state.GetFile("School/x.txt")?.BaseHash); // no longer synced
        Assert.Equal("photo", File.ReadAllText(Local("Photos/y.jpg")));

        // The next passes, with the nest's new answer: the files still stay, and nothing is sent.
        _api.Access = new AccessAnswer(level, ["Photos"]);
        r = await Pass(full: true);
        r = await Pass();
        Assert.Equal(0, r.DeletedLocal + r.Uploaded + r.Errors);
        Assert.True(File.Exists(Local("School/x.txt")));
        Assert.Equal(["Photos/y.jpg", "School/sub/y.txt", "School/x.txt"], _nest.LivePaths()); // the nest's copies are untouched

        // A file the nest says was deleted, inside the share, still goes.
        _nest.Delete("Photos/y.jpg");
        r = await Pass();
        Assert.Equal(1, r.DeletedLocal);
        Assert.False(File.Exists(Local("Photos/y.jpg")));
    }

    [Fact]
    public async Task ARefusalFromTheNestIsHeldBackNotAnError()
    {
        _engine.Access = NestAccess.Read("edit", null, "Oskar Nilsson", "Home nest"); // what the app knew
        _api.RefuseUploads = PairnetsShareRefusedException.ViewOnly; // the owner made it view only meanwhile
        var refused = new List<string>();
        _engine.AccessRefused += refused.Add;
        Write("Photos/new.jpg", "new");

        var r = await Pass();
        Assert.Equal(0, r.Errors);
        Assert.Equal(["Photos/new.jpg"], r.HeldBack);
        Assert.Equal([PairnetsShareRefusedException.ViewOnly], refused);
    }

    [Fact]
    public async Task AFullPassAsksTheNestWhatThisComputerMayDoFirst()
    {
        _engine.Access = NestAccess.Read("edit", null, "Oskar Nilsson", "Home nest");
        _api.Access = new AccessAnswer("view", ["Photos"]);
        NestAccess? told = null;
        _engine.AccessChanged += a => told = a;
        Write("Photos/new.jpg", "new");

        var r = await Pass(full: true);
        Assert.Equal(1, _api.AccessAsked);
        Assert.Equal(NestAccess.View, _engine.Access.Level);
        Assert.Equal(["Photos"], _engine.Access.Folders!);
        Assert.Equal("Oskar Nilsson", _engine.Access.SharedBy); // what the app knew of the owner stays
        Assert.Equal(_engine.Access, told);
        Assert.Equal(["Photos/new.jpg"], r.HeldBack);
        Assert.Empty(_nest.LivePaths());
    }

    [Fact]
    public async Task TheOwnersComputerNeverAsksAndSendsAsAlways()
    {
        _engine.Access = NestAccess.Full;
        Write("anything.txt", "mine");
        var r = await Pass(full: true);
        Assert.Equal(0, _api.AccessAsked);
        Assert.Equal(1, r.Uploaded);
        Assert.Empty(r.HeldBack);
    }

    public void Dispose()
    {
        _state.Dispose();
        _folder.Dispose();
        _stateDir.Dispose();
    }

    /// <summary>The in-memory nest, plus what a nest that knows friends answers about access and refuses.</summary>
    private sealed class SharedApi(FakeApi inner) : IPairnetsApi
    {
        /// <summary>The code every upload is refused with (403), or null.</summary>
        public string? RefuseUploads { get; set; }

        /// <summary>What GET /api/access answers (null: a nest from before friends).</summary>
        public AccessAnswer? Access { get; set; }

        public int AccessAsked { get; private set; }

        public string ServerKey => inner.ServerKey;

        /// <summary>The folders the nest lists to this computer now (null: everything), as a real nest filters its list.</summary>
        public IReadOnlyList<string>? ListsOnly { get; set; }

        public async Task<ManifestResponse> GetManifestAsync(long? since, CancellationToken ct)
        {
            var all = await inner.GetManifestAsync(since, ct);
            if (ListsOnly is not { } folders)
                return all;
            var share = NestAccess.Read(NestAccess.Edit, folders);
            return all with { Entries = all.Entries.Where(e => share.Allows(e.Path)).ToList() };
        }

        public Task<DownloadResult> DownloadAsync(string path, Stream destination, Action<long>? progress, CancellationToken ct, DownloadResume? resume = null) =>
            inner.DownloadAsync(path, destination, progress, ct, resume);

        public Task<(ApiResult Result, string SentHash, long SentBytes)> UploadAsync(string path, string baseHash, long mtimeMs, Stream content, Action<long>? progress,
            CancellationToken ct, UploadResume? resume = null) =>
            RefuseUploads is { } code
                ? throw new PairnetsShareRefusedException(code, FriendlyErrors.ShareRefused(code))
                : inner.UploadAsync(path, baseHash, mtimeMs, content, progress, ct, resume);

        public Task AbandonUploadAsync(string uploadId) => inner.AbandonUploadAsync(uploadId);

        public Task<ApiResult> DeleteAsync(string path, string baseHash, CancellationToken ct) => inner.DeleteAsync(path, baseHash, ct);

        public Task<AccessAnswer?> GetAccessAsync(CancellationToken ct)
        {
            AccessAsked++;
            return Task.FromResult(Access);
        }
    }
}
