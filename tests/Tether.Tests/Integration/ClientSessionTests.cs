using System.Net;
using System.Text;
using Tether.Core;
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
        // A file briefly held by another program (antivirus on Windows) is a per-file error,
        // retried after this; the 30 s default is longer than these tests wait.
        ErrorRetry = TimeSpan.FromMilliseconds(500),
        OfflineBackoff = [TimeSpan.FromMilliseconds(200)],
    }, hooks: hooks);

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
        {
            var path = Path.Combine(_folder.Path, $"first{i:00}.txt");
            File.WriteAllText(path, "a" + i);
            // Old enough that the first pass takes all 20 at once, however slowly they were written.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        }
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

    [Fact]
    public async Task SessionShowsFilesInProgressSpeedAndServerSpace()
    {
        var hooks = new EngineHooks { BeforeTransfer = (_, _) => Task.Delay(150) };
        for (var i = 0; i < 12; i++)
        {
            var path = Path.Combine(_folder.Path, $"f{i:00}.bin");
            File.WriteAllBytes(path, new byte[200_000]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1)); // all 12 in one pass
        }
        await using var session = Start(hooks);
        var sawActive = 0;
        long sawTotal = 0;
        session.StatusChanged += s =>
        {
            if (s.Active.Count > Volatile.Read(ref sawActive))
                Volatile.Write(ref sawActive, s.Active.Count);
            if (s.PassBytesTotal > Interlocked.Read(ref sawTotal))
                Interlocked.Exchange(ref sawTotal, s.PassBytesTotal);
        };

        await WaitUntil(() => _server.Store.ReadManifest(null).Entries.Count == 12, "uploads");
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "idle");
        Assert.True(Volatile.Read(ref sawActive) >= 2, "several files were shown in progress at once");
        Assert.Equal(12 * 200_000L, Interlocked.Read(ref sawTotal));
        Assert.Empty(session.Status.Active);

        await WaitUntil(() => session.Status.Server is not null, "server info");
        Assert.NotNull(session.Status.ServerFreeText);
        Assert.Equal(TetherInfo.ProductVersion, session.Status.Server!.ServerVersion);
    }

    [Fact]
    public async Task UpdateServerExplainsAServerThatCannotUpdateItself()
    {
        await using var session = Start();
        var result = await session.UpdateServerAsync(null, default);
        Assert.False(result.Success);
        Assert.False(result.CanUpdateItself);
    }

    [Fact]
    public async Task UpdateServerFollowsTheUpdaterAndReportsItsFailure()
    {
        await _server.DisposeAsync();
        using var dir = new TempDir("updater");
        var script = Path.Combine(dir.Path, "update.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        _server = await TestServer.StartAsync(config: new() { ["Sync:UpdaterScript"] = script });
        await using var session = Start();
        var steps = new List<string>();

        // Play the root updater: pick up the request and report a failure (status first, as update.sh does).
        var updateDir = Path.Combine(_server.Paths.DataDir, "update");
        PlayUpdater(updateDir, "{\"state\":\"failed\",\"message\":\"The download did not match its checksum. Nothing was changed.\"}",
            $"2026-10-06T09:00:00Z checksum line: <missing>\n  Token:       {_server.Token}\n");
        var result = await session.UpdateServerAsync(new Progress<string>(steps.Add), default, pollInterval: TimeSpan.FromMilliseconds(100), timeout: TimeSpan.FromSeconds(10));
        Assert.False(result.Success);
        Assert.True(result.CanUpdateItself);
        Assert.Contains("checksum", result.Message);

        // A failure always carries the server's updater report, with the token hidden.
        Assert.NotNull(result.Details);
        Assert.Contains("update.sh present:   yes", result.Details);
        Assert.Contains("checksum line: <missing>", result.Details);
        Assert.Contains("Token:       (hidden)", result.Details);
        Assert.DoesNotContain(_server.Token, result.Details);
    }

    [Fact]
    public async Task UpdateServerSaysAlreadyUpToDateAndTracesEachStep()
    {
        await _server.DisposeAsync();
        using var dir = new TempDir("updater");
        var script = Path.Combine(dir.Path, "update.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        _server = await TestServer.StartAsync(config: new() { ["Sync:UpdaterScript"] = script });
        await using var session = Start();
        await WaitUntil(() => session.Status.Server is not null, "server info");

        PlayUpdater(Path.Combine(_server.Paths.DataDir, "update"),
            $"{{\"state\":\"succeeded\",\"message\":\"Already up to date ({TetherInfo.ProductVersion}).\"}}", null);
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var result = await session.UpdateServerAsync(null, default, pollInterval: TimeSpan.FromMilliseconds(100), timeout: TimeSpan.FromSeconds(10),
            trace: new Progress<string>(trace.Enqueue));

        Assert.True(result.Success);
        Assert.True(result.AlreadyUpToDate);
        Assert.Equal(TetherInfo.ProductVersion, result.ServerVersion);
        Assert.Contains("update.sh present", result.Details); // with a trace (Debug mode) the report comes along on success too
        await WaitUntil(() => trace.Any(l => l.Contains("Done: Already up to date")), "trace of the result");
        Assert.Contains(trace, l => l.Contains("Asking the server to update"));
        Assert.Contains(trace, l => l.Contains("accepted"));
    }

    [Fact]
    public async Task ASecondClickFollowsTheRequestStillUnderWay()
    {
        await _server.DisposeAsync();
        using var dir = new TempDir("updater");
        var script = Path.Combine(dir.Path, "update.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        _server = await TestServer.StartAsync(config: new() { ["Sync:UpdaterScript"] = script });
        await using var session = Start();
        var updateDir = Path.Combine(_server.Paths.DataDir, "update");

        // The first click's request is waiting; the second click is "too soon" but follows it.
        using (var http = _server.RawHttp(_server.Token))
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("api/update", null)).StatusCode);
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            File.WriteAllText(Path.Combine(updateDir, "status.json"), "{\"state\":\"failed\",\"message\":\"Could not download the release.\"}");
            File.Delete(Path.Combine(updateDir, "request"));
        });
        var result = await session.UpdateServerAsync(null, default, pollInterval: TimeSpan.FromMilliseconds(100), timeout: TimeSpan.FromSeconds(10));
        Assert.False(result.Success);
        Assert.Equal("Could not download the release.", result.Message);
    }

    [Fact]
    public async Task UpdateServerOnAClosedSessionExplainsInsteadOfThrowing()
    {
        // Saving Settings disposes the session; an update window opened before that used to crash here.
        var session = Start();
        await session.DisposeAsync();
        var result = await session.UpdateServerAsync(null, default, pollInterval: TimeSpan.FromMilliseconds(100), timeout: TimeSpan.FromSeconds(2));
        Assert.False(result.Success);
        Assert.Contains("connection to the server was restarted", result.Message);
        Assert.Contains("Could not read the server's updater details", result.Details);
    }

    [Fact]
    public async Task BugReportCarriesStatusActivityAndServerDetailsButNotTheToken()
    {
        await using var session = Start();
        await WaitUntil(() => session.Status.Server is not null, "server info");
        session.Activity.Add(ActivityKind.Info, null, "Something happened");
        var report = await BugReport.BuildAsync(session.Settings, session, null, null, "Test app");

        Assert.Contains("App:       Test app " + TetherInfo.ProductVersion, report);
        Assert.Contains("Server:       " + TetherInfo.ProductVersion, report);
        Assert.Contains("update.sh present:   no", report); // the server's updater report came along
        Assert.Contains("Something happened", report);
        Assert.Contains("(no log file yet)", report);
        Assert.DoesNotContain(_server.Token, report);
        Assert.DoesNotContain("unused-by-session", report); // the protected token from the settings
    }

    /// <summary>Acts like update.sh: waits for the request, writes the log and status, then removes the request.</summary>
    private static void PlayUpdater(string updateDir, string statusJson, string? log) => _ = Task.Run(async () =>
    {
        while (!File.Exists(Path.Combine(updateDir, "request")))
            await Task.Delay(50);
        if (log is not null)
            File.WriteAllText(Path.Combine(updateDir, "update.log"), log);
        File.WriteAllText(Path.Combine(updateDir, "status.json"), statusJson);
        File.Delete(Path.Combine(updateDir, "request"));
    });
}
