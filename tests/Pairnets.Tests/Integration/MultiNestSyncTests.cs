using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// One app syncing two nests at once, each into its own folder (docs/DECISIONS.md, "Several nests at once"): both run side
/// by side in one process against two real servers, and no file ever reaches the other nest.
/// </summary>
public class MultiNestSyncTests : IAsyncLifetime
{
    private TestServer _home = null!;
    private TestServer _work = null!;
    private readonly TempDir _homeFolder = new("multinest-home");
    private readonly TempDir _workFolder = new("multinest-work");
    private readonly TempDir _stateBase = new("multinest-state");

    public async Task InitializeAsync()
    {
        _home = await TestServer.StartAsync();
        _work = await TestServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _home.DisposeAsync();
        await _work.DisposeAsync();
        _homeFolder.Dispose();
        _workFolder.Dispose();
        _stateBase.Dispose();
    }

    private static RunnerOptions Fast(RunnerOptions o) => new()
    {
        ServerUrl = o.ServerUrl,
        Token = o.Token,
        DeviceId = o.DeviceId,
        WatcherDebounce = TimeSpan.FromMilliseconds(200),
        PeriodicInterval = TimeSpan.FromHours(1),
        UnstableRetry = TimeSpan.FromMilliseconds(300),
        ErrorRetry = TimeSpan.FromMilliseconds(500),
        OfflineBackoff = [TimeSpan.FromMilliseconds(200)],
    };

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30); // slow CI runners
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task TwoNestsSyncTheirOwnFoldersSideBySide()
    {
        var settings = new ClientSettings
        {
            ServerUrl = _home.Url.ToString(),
            ProtectedToken = "unused",
            Folder = _homeFolder.Path,
            DeviceName = "StudyPC",
            NestLabel = "Home nest",
            FirstRunCompleted = true,
        };
        var work = settings.BlankNest();
        work.ServerUrl = _work.Url.ToString();
        work.ProtectedToken = "unused";
        work.Folder = _workFolder.Path;
        work.DeviceName = "STUDY-LAPTOP";
        work.NestLabel = "Work nest";
        settings.AddNest(work);
        var nests = settings.Nests();
        Assert.Equal(2, nests.Count);

        File.WriteAllText(Path.Combine(_homeFolder.Path, "holiday.jpg"), "home");
        File.WriteAllText(Path.Combine(_workFolder.Path, "report.docx"), "work");
        var limits = new SpeedLimits();
        await using var home = ClientSession.Start(nests[0], _home.Token, new PermanentDeleteTrash(), stateBaseDir: _stateBase.Path, runnerOptions: Fast, limits: limits);
        await using var other = ClientSession.Start(nests[1], _work.Token, new PermanentDeleteTrash(), stateBaseDir: _stateBase.Path, runnerOptions: Fast, limits: limits);

        await WaitUntil(() => _home.Store.ReadManifest(null).Entries.Any(e => e.Path == "holiday.jpg"), "home upload");
        await WaitUntil(() => _work.Store.ReadManifest(null).Entries.Any(e => e.Path == "report.docx"), "work upload");
        await WaitUntil(() => home.Status.Status == RunnerStatus.Idle && other.Status.Status == RunnerStatus.Idle, "both idle");

        // Each nest has only its own folder's files.
        Assert.DoesNotContain(_home.Store.ReadManifest(null).Entries, e => e.Path == "report.docx");
        Assert.DoesNotContain(_work.Store.ReadManifest(null).Entries, e => e.Path == "holiday.jpg");
        Assert.Equal("StudyPC", home.Settings.DeviceName);
        Assert.Equal("STUDY-LAPTOP", other.Settings.DeviceName);

        // Their sync notes are apart, and they share one speed limit.
        Assert.NotEqual(home.StateDirectory, other.StateDirectory);
        Assert.Same(home.Api.UploadLimit, other.Api.UploadLimit);
        Assert.False(File.Exists(Path.Combine(_homeFolder.Path, "report.docx")));
        Assert.False(File.Exists(Path.Combine(_workFolder.Path, "holiday.jpg")));
    }
}
