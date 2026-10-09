using Microsoft.AspNetCore.Http;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Hashing;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// A computer signed in with a Pairnets account syncs with its server through the service's relay address
/// (<c>/n/&lt;nest id&gt;/</c>): files, uploads in pieces and the push channel, and plain words when the server is away.
/// </summary>
public sealed class RelaySyncTests : IAsyncLifetime
{
    private TestServer _nest = null!;
    private FakeSyncService _service = null!;
    private readonly TempDir _folder = new("relay");
    private readonly TempDir _stateBase = new("relay-state");

    public async Task InitializeAsync()
    {
        _nest = await TestServer.StartAsync();
        _service = await FakeSyncService.StartAsync(_nest);
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        await _nest.DisposeAsync();
        _folder.Dispose();
        _stateBase.Dispose();
    }

    private async Task<AccountSignInResult> SignInAsync()
    {
        var done = await new AccountSignIn("LAPTOP", "email", _service.Url, interval: TimeSpan.FromMilliseconds(50))
            .RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Approved, done.Stage);
        return done.Result!;
    }

    private ClientSettings Settings(AccountSignInResult result) => AccountSignIn.SettingsAfterSignIn(null, result, _folder.Path, false, "unused-by-session");

    private ClientSession Start(ClientSettings settings, string key, Uri? service) => ClientSession.Start(settings, key, new PermanentDeleteTrash(),
        stateBaseDir: _stateBase.Path, runnerOptions: o => new RunnerOptions
        {
            ServerUrl = o.ServerUrl,
            Token = o.Token,
            DeviceId = o.DeviceId,
            WatcherDebounce = TimeSpan.FromMilliseconds(200),
            PeriodicInterval = TimeSpan.FromHours(1),
            UnstableRetry = TimeSpan.FromMilliseconds(300),
            ErrorRetry = TimeSpan.FromMilliseconds(500),
            OfflineBackoff = [TimeSpan.FromMilliseconds(200)],
        }, serviceUrl: service);

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

    [Fact]
    public async Task AnAccountSignInSyncsFilesThroughTheRelayAndHearsTheOtherComputersThroughIt()
    {
        var result = await SignInAsync();
        File.WriteAllText(Path.Combine(_folder.Path, "from-laptop.txt"), "hello");
        await using var session = Start(Settings(result), result.Device.Key, _service.Url);
        Assert.True(session.Status.IsRelay);
        Assert.Equal(FakeSyncService.Email, session.Status.AccountEmail);

        await WaitUntil(() => _nest.Store.ReadManifest(null).Entries.Any(e => e.Path == "from-laptop.txt" && !e.Deleted), "upload through the relay");
        await WaitUntil(() => session.Runner.HubConnected, "push channel through the relay");
        Assert.True(_service.HubSockets >= 1, "the push channel is a WebSocket through /n/<nest id>/hub");
        Assert.Contains("POST /hub/negotiate", _service.Relayed);
        Assert.Contains("GET /api/manifest", _service.Relayed);
        Assert.Contains("PUT /api/file", _service.Relayed);
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "idle");

        // Another computer adds a file. Nothing but the push through the relay makes this one look (the next
        // regular pass is an hour away), so the file arriving here proves the push channel works through it.
        var desktop = _nest.MintKey("DESKTOP");
        using (var other = _nest.Client("DESKTOP", desktop.Key))
        {
            var (added, _, _) = await other.UploadAsync("from-desktop.txt", "none", 1, new MemoryStream("hi"u8.ToArray()), null, CancellationToken.None);
            Assert.Equal(ApiOutcome.Ok, added.Outcome);
        }
        var arrived = Path.Combine(_folder.Path, "from-desktop.txt");
        await WaitUntil(() => File.Exists(arrived) && File.ReadAllText(arrived) == "hi", "download after a push through the relay");

        // Nothing moved the computer off its relay address on the way.
        Assert.Equal(_service.RelayUrl.ToString(), session.Settings.ServerUrl);
    }

    [Fact]
    public async Task UploadsInPiecesAndDownloadsGoThroughTheRelayPrefix()
    {
        var result = await SignInAsync();
        using var api = new PairnetsApiClient(result.ServerUrl, result.Device.Key, "LAPTOP", pieceSize: 64 * 1024, minPieceSize: 64 * 1024, serviceUrl: _service.Url);
        var data = new byte[200_000];
        new Random(7).NextBytes(data);

        var (uploaded, hash, sent) = await api.UploadAsync("big.bin", "none", 1, new MemoryStream(data), null, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, uploaded.Outcome);
        Assert.Equal((ContentHash.Of(data), 200_000L), (hash, sent));
        Assert.Contains("POST /api/upload", _service.Relayed);
        Assert.Equal(4, _service.Relayed.Count(r => r.StartsWith("PUT /api/upload/", StringComparison.Ordinal)));
        Assert.Contains(_service.Relayed, r => r.StartsWith("POST /api/upload/", StringComparison.Ordinal) && r.EndsWith("/commit", StringComparison.Ordinal));
        using var back = new MemoryStream();
        Assert.True((await api.DownloadAsync("big.bin", back, null, CancellationToken.None)).Found);
        Assert.Equal(data, back.ToArray());
    }

    [Fact]
    public async Task ARelayedComputerNeverMovesToAnotherAddressTheServerNames()
    {
        // The server names another address that leads to it as well (its own direct one); a nest on its own domain does
        // this when it gets an HTTPS name, and its computers move there.
        _service.Intercept = async (ctx, path) =>
        {
            if (path != "/api/hello")
                return false;
            await ctx.Response.WriteAsJsonAsync(new ServerHello(PairnetsInfo.ProductName, PairnetsInfo.ApiVersion, PairnetsInfo.ProductVersion,
                _nest.Url.ToString(), DeviceKeys: true, SignIn: false), PairnetsJson.Options);
            return true;
        };
        var result = await SignInAsync();

        // Where the relay address is not recognised (another service), the computer moves, as it always did.
        await using (var unrecognised = Start(Settings(result), result.Device.Key, service: null))
        {
            Assert.False(unrecognised.Status.IsRelay);
            var moved = new TaskCompletionSource<ClientSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            unrecognised.AccountChanged += (next, _) => moved.TrySetResult(next);
            Assert.Equal(_nest.Url.ToString(), (await moved.Task.WaitAsync(TimeSpan.FromSeconds(30))).ServerUrl);
        }

        var hellos = _service.Relayed.Count(r => r == "GET /api/hello");
        await using var relayed = Start(Settings(result), result.Device.Key, _service.Url);
        var changed = 0;
        relayed.AccountChanged += (_, _) => Interlocked.Increment(ref changed);
        await WaitUntil(() => relayed.Status.Server is not null && _service.Relayed.Count(r => r == "GET /api/hello") > hellos, "the start-up account check");
        await relayed.CheckAccountAsync(relayed.Status.Server!);
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.True(relayed.Status.IsRelay);
        Assert.Equal(_service.RelayUrl.ToString(), relayed.Settings.ServerUrl);
    }

    [Fact]
    public async Task AServerThatIsOffOrNoLongerLinkedSaysSoAndSyncingComesBackOnItsOwn()
    {
        var result = await SignInAsync();
        await using var session = Start(Settings(result), result.Device.Key, _service.Url);
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle && session.Status.LastSyncAt is not null, "first sync");

        _service.NestOnline = false;
        session.SyncNow();
        await WaitUntil(() => session.Status.Status == RunnerStatus.Offline, "offline");
        Assert.Equal(ServiceErrors.NestOfflineMessage, session.Status.DetailText);
        Assert.Equal("Not connected", session.Status.ConnectionText);
        Assert.Contains(session.Activity.Items, i => i.Kind == ActivityKind.Offline && i.Text.Contains(ServiceErrors.NestOfflineMessage, StringComparison.Ordinal));
        Assert.DoesNotContain(session.Activity.Items, i => i.Text.Contains("tunnel", StringComparison.OrdinalIgnoreCase));

        var test = await PairnetsApiClient.TestConnectionAsync(result.ServerUrl.ToString(), result.Device.Key, "LAPTOP");
        Assert.Equal(ConnectionTestStatus.Unreachable, test.Status);

        _service.NestOnline = true;
        _service.NestKnown = false;
        await WaitUntil(() => session.Status.Status == RunnerStatus.Blocked, "blocked");
        Assert.Equal(BlockReason.SignedOut, session.Status.BlockReason);
        Assert.Equal(ServiceErrors.NestUnknownMessage, session.Status.DetailText);
        Assert.Equal("Sign in again…", session.Status.FixLabel);

        _service.NestKnown = true;
        session.SyncNow();
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle && session.Status.BlockReason == BlockReason.None, "syncing again");
    }

    [Fact]
    public async Task TheConnectionTestWorksThroughTheRelay()
    {
        var result = await SignInAsync();
        var ok = await PairnetsApiClient.TestConnectionAsync(result.ServerUrl.ToString(), result.Device.Key, "LAPTOP");
        Assert.Equal(ConnectionTestStatus.Ok, ok.Status);
        Assert.Contains("GET /api/health", _service.Relayed);
        Assert.Contains("GET /api/info", _service.Relayed);

        _service.NestOnline = false;
        var off = await PairnetsApiClient.TestConnectionAsync(result.ServerUrl.ToString(), result.Device.Key, "LAPTOP");
        Assert.Equal((ConnectionTestStatus.Unreachable, ServiceErrors.NestOfflineMessage), (off.Status, off.Message));
    }
}
