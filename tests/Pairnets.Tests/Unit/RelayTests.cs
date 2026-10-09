using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Relay mode (Pairnets Cloud, version 2): recognising a relay address, keeping its path, and what changes in it.</summary>
public class RelayTests
{
    private const string Id = "nst_testnest000000000000000001";
    private const string Address = "https://sync.pairnets.app/n/" + Id + "/";

    [Theory]
    [InlineData(Address, Id)]
    [InlineData("https://sync.pairnets.app/n/" + Id, Id)]
    [InlineData("HTTPS://SYNC.PAIRNETS.APP/n/" + Id + "/", Id)]
    [InlineData("https://sync.pairnets.app:443/n/" + Id + "/", Id)]
    [InlineData("https://sync.pairnets.app/n/" + Id + "/api/info", null)]
    [InlineData("https://sync.pairnets.app/n/nst_TESTNEST000000000000000001/", null)]
    [InlineData("https://sync.pairnets.app/n/nst_short/", null)]
    [InlineData("https://sync.pairnets.app/n/nst_testnest00000000000000000i/", null)]
    [InlineData("https://sync.pairnets.app/x/" + Id + "/", null)]
    [InlineData("https://sync.pairnets.app/n/" + Id + "/?a=1", null)]
    [InlineData("http://sync.pairnets.app/n/" + Id + "/", null)]
    [InlineData("https://sync.pairnets.app:8443/n/" + Id + "/", null)]
    [InlineData("https://nest.example.com/n/" + Id + "/", null)]
    [InlineData("https://sync.pairnets.app/", null)]
    [InlineData("https://nest.example.com/", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RelayAddressesAreTheServicesOwnNPaths(string? serverUrl, string? nestId)
    {
        Assert.Equal(nestId, Relay.NestId(serverUrl));
        Assert.Equal(nestId is not null, Relay.IsRelayAddress(serverUrl));
    }

    [Fact]
    public void TheServiceAddressCanBeAnotherOne()
    {
        var service = new Uri("http://127.0.0.1:5099/");
        Assert.Equal(Id, Relay.NestId($"http://127.0.0.1:5099/n/{Id}/", service));
        Assert.Null(Relay.NestId(Address, service));
        Assert.Null(Relay.NestId($"http://127.0.0.1:5099/n/{Id}/"));
        Assert.Equal(new Uri($"http://127.0.0.1:5099/n/{Id}/"), Relay.AddressOf(Id, service));
        Assert.Equal(new Uri(Address), Relay.AddressOf(Id));
        Assert.Throws<ArgumentException>(() => Relay.AddressOf("nst_bad"));
        Assert.Equal("http://127.0.0.1:5099/account", Relay.AccountUrl(service));
    }

    [Fact]
    public void ManageComputersOpensTheAccountPageInRelayModeAndTheNestsDevicesPageOtherwise()
    {
        Assert.Equal("https://sync.pairnets.app/account", Relay.ManageComputersUrl(Address, null));
        Assert.Equal("https://sync.pairnets.app/account", Relay.ManageComputersUrl(Address, "https://sync.pairnets.app"));
        Assert.Equal("https://nest.example.com/devices", Relay.ManageComputersUrl("https://nest.example.com/", "https://nest.example.com"));
        Assert.Equal("https://nest.example.com/devices", Relay.ManageComputersUrl("http://192.0.2.4:5075/", "https://nest.example.com/"));
        Assert.Null(Relay.ManageComputersUrl("http://192.0.2.4:5075/", null));
    }

    [Fact]
    public void ANestThatCouldNotBeToldPointsToTheAccountPageInRelayMode()
    {
        // Signing out or resetting while the nest cannot be reached: where to remove this computer by hand.
        Assert.Equal("Remove this computer on your account page (https://sync.pairnets.app/account) so its key stops working there too.",
            Relay.RemoveByHandHint(Address));
        Assert.Equal("Remove this computer on your nest's Devices page so its key stops working there too.",
            Relay.RemoveByHandHint("https://nest.example.com/"));
        Assert.Contains("Devices page", Relay.RemoveByHandHint(null));
        Assert.Contains("(http://127.0.0.1:5/account)", Relay.RemoveByHandHint("http://127.0.0.1:5/n/" + Id + "/", new Uri("http://127.0.0.1:5/")));

        // "Make an account" in the apps: accounts are made by signing in.
        Assert.Equal("https://sync.pairnets.app/login", Relay.LoginUrl());
        Assert.Equal("http://127.0.0.1:5/login", Relay.LoginUrl(new Uri("http://127.0.0.1:5/")));
    }

    [Fact]
    public void AddAnotherComputerSaysToSignInWithTheSameAccountInRelayMode()
    {
        var relay = Relay.AddComputerSteps(Address, "you@example.com", null);
        Assert.Contains("Install Pairnets", relay);
        Assert.Contains("Continue with email (or Google) with the same account (you@example.com)", relay);
        Assert.DoesNotContain("sync.pairnets.app", relay); // nothing to type: the account finds the server
        Assert.Contains("with the same account.", Relay.AddComputerSteps(Address, null, null));

        var own = Relay.AddComputerSteps("https://nest.example.com/", null, "https://nest.example.com");
        Assert.Contains("type nest.example.com and press \"Sign in with your browser\"", own);
        Assert.Contains("type your nest's name", Relay.AddComputerSteps("http://192.0.2.4:5075/", null, null));
    }

    [Fact]
    public void ARelayAddressKeepsItsPathWhereverAnAddressIsCleanedUp()
    {
        Assert.Equal(Address, PairnetsApiClient.NormalizeBase(new Uri("https://sync.pairnets.app/n/" + Id)).ToString());
        Assert.True(PairnetsApiClient.TryParseServerUrl(" https://sync.pairnets.app/n/" + Id + " ", out var parsed));
        Assert.Equal(Address, parsed!.ToString());
        Assert.Equal(Address, Nest.ParseAddress("https://sync.pairnets.app/n/" + Id)!.ToString());
        Assert.Equal(Address, Nest.ParseAddress("sync.pairnets.app/n/" + Id + "/")!.ToString());
        Assert.Equal(new Uri($"https://other.example/n/{Id}/"), Nest.ParseAddress($"other.example/n/{Id}"));
        // Anything else after a typed name is still dropped: a link copied from the nest's website finds the nest.
        Assert.Equal("https://nest.pairnets.app/", Nest.ParseAddress("nest.pairnets.app/link?code=ABCD-EFGH")!.ToString());
        Assert.Equal("https://nest.pairnets.app/", Nest.ParseAddress("nest.pairnets.app/devices")!.ToString());

        Assert.Equal(new Uri(Address + "hub"), SyncRunner.HubAddress(new Uri("https://sync.pairnets.app/n/" + Id)));
        Assert.Equal(new Uri("https://nest.example.com/hub"), SyncRunner.HubAddress(new Uri("https://nest.example.com")));

        var grant = new DeviceKeyGrant("dev-1", "LAPTOP", "unused-key");
        var settings = Nest.SettingsAfterSignIn(null, new Uri("https://sync.pairnets.app/n/" + Id), grant, "/data", false, "protected");
        Assert.Equal(Address, settings.ServerUrl);
        Assert.Null(settings.AccountEmail);
    }

    [Fact]
    public void TheAccountEmailIsSavedAndKeptOnlyWhileTheSameServerAndKeyAre()
    {
        using var dir = new TempDir("relay-settings");
        var result = new AccountSignInResult(new Uri(Address), Id, "soro", new DeviceKeyGrant("dev-1", "LAPTOP", "unused-key"), "you@example.com");
        var settings = AccountSignIn.SettingsAfterSignIn(new ClientSettings { AccountEmail = "old@example.com", ParallelTransfers = 8 }, result, dir.Path, true, "protected");
        Assert.Equal((Address, "dev-1", "LAPTOP", "you@example.com", 8), (settings.ServerUrl, settings.DeviceId, settings.DeviceName, settings.AccountEmail, settings.ParallelTransfers));
        Assert.True(settings.IsComplete);

        var path = dir.Combine("settings.json");
        SettingsStore.Save(path, settings);
        var loaded = SettingsStore.Load(path);
        Assert.Equal((Address, "you@example.com"), (loaded.ServerUrl, loaded.AccountEmail));
        Assert.Equal("you@example.com", loaded.Clone().AccountEmail);

        // Settings saved again: kept with the same key on the same server, gone with a typed token or another address.
        Assert.Equal("you@example.com", OwnKey.AccountEmailAfterSave(loaded, new Uri("https://sync.pairnets.app/n/" + Id), "dev-1"));
        Assert.Null(OwnKey.AccountEmailAfterSave(loaded, new Uri(Address), null));
        Assert.Null(OwnKey.AccountEmailAfterSave(loaded, new Uri(Address), "dev-2"));
        Assert.Null(OwnKey.AccountEmailAfterSave(loaded, new Uri("https://nest.example.com/"), "dev-1"));
    }

    [Fact]
    public async Task TypingTheServicesOwnAddressAsANestSaysToUseTheAccount()
    {
        var service = await Nest.CheckAsync("sync.pairnets.app");
        Assert.Equal(NestCheckStatus.NoSignIn, service.Status);
        Assert.Equal(Nest.AccountAddressMessage, service.Message);
        Assert.False(service.CanSignIn);
        Assert.Equal(NestCheckStatus.NoSignIn, (await Nest.CheckAsync(Address)).Status);
    }

    // ------------------------------------------------------------------ requests under the /n/<nest id>/ prefix

    /// <summary>Answers like a nest, from memory, and remembers the path of every request.</summary>
    private sealed class RecordingNest(Func<HttpRequestMessage, HttpResponseMessage?>? answer = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths)
                Paths.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (answer?.Invoke(request) is { } given)
                return given;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"serverId\":\"srv\",\"version\":1,\"apiVersion\":1}");
            if (request.Method == HttpMethod.Post && path.EndsWith("/api/upload", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, "{\"id\":\"u1\",\"received\":0}");
            if (request.Method == HttpMethod.Put && path.EndsWith("/api/upload/u1", StringComparison.Ordinal))
            {
                var offset = long.Parse(request.RequestUri.Query.Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
                var length = (await request.Content!.ReadAsByteArrayAsync(cancellationToken)).Length;
                return Json(HttpStatusCode.OK, $"{{\"id\":\"u1\",\"received\":{offset + length}}}");
            }
            if (path.EndsWith("/api/upload/u1/commit", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"path\":\"big.bin\",\"hash\":\"h\",\"size\":3000,\"modifiedMs\":1,\"deleted\":false,\"version\":2}");
            return Json(HttpStatusCode.NotFound, "{\"code\":\"not-found\"}");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The Pairnets service's own error, as Cloudflare delivers it (its headers on, but JSON: not one of its pages).</summary>
    private static HttpResponseMessage ServiceError(HttpStatusCode status, string code)
    {
        var resp = Json(status, $"{{\"error\":\"{code}\",\"message\":\"Words from the service.\"}}");
        resp.Headers.Server.Add(new ProductInfoHeaderValue("cloudflare", null));
        resp.Headers.Add("CF-RAY", "8f00000000000000-AMS");
        return resp;
    }

    [Fact]
    public async Task EveryRequestAndThePushChannelGoUnderTheRelayPrefix()
    {
        var nest = new RecordingNest();
        using var api = new PairnetsApiClient(new Uri("https://sync.pairnets.app/n/" + Id), "unused-key", "pc", nest, pieceSize: 1024, minPieceSize: 1024);
        Assert.True(api.IsRelay);
        Assert.Equal(new Uri(Address), api.BaseAddress);

        await api.GetInfoAsync(CancellationToken.None);
        var (result, _, sent) = await api.UploadAsync("big.bin", "none", 1, new MemoryStream(new byte[3000]), null, CancellationToken.None);
        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(3000, sent);

        var prefix = "/n/" + Id;
        Assert.Equal(
            [$"GET {prefix}/api/info", $"POST {prefix}/api/upload", $"PUT {prefix}/api/upload/u1", $"PUT {prefix}/api/upload/u1", $"PUT {prefix}/api/upload/u1", $"POST {prefix}/api/upload/u1/commit"],
            nest.Paths);
        Assert.Equal(prefix + "/hub", SyncRunner.HubAddress(api.BaseAddress).AbsolutePath);
        using var own = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc", new RecordingNest());
        Assert.False(own.IsRelay);
    }

    [Fact]
    public async Task AServerThatIsNotConnectedReadsAsNotConnectedEverywhere()
    {
        var nest = new RecordingNest(_ => ServiceError(HttpStatusCode.ServiceUnavailable, ServiceErrors.NestOffline));
        using var api = new PairnetsApiClient(new Uri(Address), "unused-key", "pc", nest);

        var info = await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetInfoAsync(CancellationToken.None));
        Assert.Equal(ServiceErrors.NestOfflineMessage, info.Message);
        Assert.Equal(ServiceErrors.NestOffline, info.Code);
        Assert.StartsWith("Your server is not connected right now", info.Message);
        Assert.DoesNotContain("tunnel", info.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetHelloAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetManifestAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.DownloadAsync("a.txt", new MemoryStream(), null, CancellationToken.None));

        var test = await PairnetsApiClient.TestConnectionAsync(Address, "unused-key", "pc", new RecordingNest(_ => ServiceError(HttpStatusCode.ServiceUnavailable, ServiceErrors.NestOffline)));
        Assert.Equal((ConnectionTestStatus.Unreachable, ServiceErrors.NestOfflineMessage), (test.Status, test.Message));
    }

    [Fact]
    public async Task AServerNoLongerLinkedIsNeverMistakenForAnOldServerOrAMissingFile()
    {
        var nest = new RecordingNest(_ => ServiceError(HttpStatusCode.NotFound, ServiceErrors.NestUnknown));
        using var api = new PairnetsApiClient(new Uri(Address), "unused-key", "pc", nest);

        var hello = await Assert.ThrowsAsync<PairnetsAuthException>(() => api.GetHelloAsync(CancellationToken.None));
        Assert.True(hello.NeedsSignIn);
        Assert.Equal(ServiceErrors.NestUnknown, hello.Code);
        Assert.StartsWith("This server is not linked to Pairnets any more", hello.Message);
        // Each of these used to read a 404 as "a server too old for this", "no such file" or "already gone".
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.GetDevicesAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.GetMeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.GetUpdateDiagnosticsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.StartPairingAsync(new PairStartRequest("pc"), CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.PollPairingAsync("poll", CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.RemoveDeviceAsync("dev-1", CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.RequestServerUpdateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.DownloadAsync("a.txt", new MemoryStream(), null, CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsAuthException>(() => api.DeleteAsync("a.txt", "h", CancellationToken.None));
        using var pieces = new PairnetsApiClient(new Uri(Address), "unused-key", "pc", new RecordingNest(_ => ServiceError(HttpStatusCode.NotFound, ServiceErrors.NestUnknown)), pieceSize: 1024, minPieceSize: 1024);
        await Assert.ThrowsAsync<PairnetsAuthException>(() => pieces.UploadAsync("big.bin", "none", 1, new MemoryStream(new byte[3000]), null, CancellationToken.None));

        var test = await PairnetsApiClient.TestConnectionAsync(Address, "unused-key", "pc", new RecordingNest(_ => ServiceError(HttpStatusCode.NotFound, ServiceErrors.NestUnknown)));
        Assert.Equal((ConnectionTestStatus.Unreachable, ServiceErrors.NestUnknownMessage), (test.Status, test.Message));
    }

    [Fact]
    public async Task ANestOnItsOwnDomainIsReadAsBefore()
    {
        // A plain 404 is a server from before the endpoint; the nest's own JSON 404 is "no such file"; its 503 is its own.
        using var old = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc",
            new RecordingNest(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") }));
        Assert.Null(await old.GetHelloAsync(CancellationToken.None));
        Assert.Null(await old.GetDevicesAsync(CancellationToken.None));
        Assert.Null(await old.GetMeAsync(CancellationToken.None));

        using var own = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc", new RecordingNest());
        Assert.False((await own.DownloadAsync("a.txt", new MemoryStream(), null, CancellationToken.None)).Found);
        // Looking for a service error first leaves the nest's own error for the next reader.
        Assert.Equal(ApiOutcome.NotFound, (await own.DeleteAsync("a.txt", "h", CancellationToken.None)).Outcome);
        Assert.Equal(ApiOutcome.NotFound, (await own.RestoreAsync("a.txt", "v1", CancellationToken.None)).Outcome);

        using var busy = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc",
            new RecordingNest(_ => Json(HttpStatusCode.ServiceUnavailable, "{\"code\":\"busy\",\"message\":\"Busy.\"}")));
        var error = await Assert.ThrowsAsync<PairnetsNetworkException>(() => busy.GetInfoAsync(CancellationToken.None));
        Assert.Null(error.Code);
        Assert.StartsWith("Server error 503", error.Message);
    }

    private static HttpResponseMessage CloudflarePage(HttpStatusCode status)
    {
        var resp = new HttpResponseMessage(status) { Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html") };
        resp.Headers.Server.Add(new ProductInfoHeaderValue("cloudflare", null));
        resp.Headers.Add("CF-RAY", "8f00000000000000-AMS");
        return resp;
    }

    [Fact]
    public async Task CloudflarePagesInFrontOfTheServiceDoNotSendYouToATunnelYouDoNotHave()
    {
        Assert.Contains("pairnets-tunnel", PairnetsApiClient.DescribeCloudflareError(CloudflarePage(HttpStatusCode.BadGateway)));
        var relay = PairnetsApiClient.DescribeCloudflareError(CloudflarePage(HttpStatusCode.BadGateway), relay: true);
        Assert.Equal("Pairnets is having trouble right now (error 502). Syncing goes on by itself when it is back.", relay);

        using var api = new PairnetsApiClient(new Uri(Address), "unused-key", "pc", new RecordingNest(_ => CloudflarePage((HttpStatusCode)530)));
        var error = await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetInfoAsync(CancellationToken.None));
        Assert.DoesNotContain("tunnel", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error 530", error.Message);
    }
}
