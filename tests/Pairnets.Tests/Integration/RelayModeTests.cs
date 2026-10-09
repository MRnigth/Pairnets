using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Server;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;
using static Pairnets.Tests.Infrastructure.RelayFixtures;

namespace Pairnets.Tests.Integration;

/// <summary>
/// A nest linked to a Pairnets account (cloud/RELAY.md §3 and §4), against the real server: what /api/hello says, the
/// service's signed calls end to end (a computer added, used with its key, listed and removed), every way an unsigned
/// or wrongly signed call is refused, and the caller's address the service passes on.
/// </summary>
public sealed class RelayModeTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync(config: Config());

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using var http = _server.RawHttp();
        return await http.SendAsync(request);
    }

    private async Task AssertRefusedAsync(HttpRequestMessage request, string why)
    {
        var response = await SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{why}: {response.StatusCode}");
        Assert.Equal("""{"error":"bad_signature"}""", await response.Content.ReadAsStringAsync()); // nothing about which check failed
    }

    [Fact]
    public async Task HelloSaysTheNestIsLinkedAndHasNoNameOfItsOwn()
    {
        using var anonymous = _server.Client(token: "nothing");
        var hello = await anonymous.GetHelloAsync(CancellationToken.None);

        Assert.Equal(new RelayHello(NestId, "https://sync.example.com"), hello!.Relay);
        Assert.Null(hello.PublicUrl);
        Assert.False(hello.SignIn);

        // A nest that is not linked says nothing about a relay at all.
        await using var plain = await TestServer.StartAsync(config: new() { ["Sync:PublicUrl"] = "https://nest.example.test" });
        using var raw = plain.RawHttp();
        var json = await raw.GetStringAsync("api/hello");
        Assert.DoesNotContain("relay", json);
        using var plainApi = plain.Client(token: "nothing");
        Assert.Null((await plainApi.GetHelloAsync(CancellationToken.None))!.Relay);
    }

    [Fact]
    public async Task ComputersJoinOnlyThroughTheAccountInRelayMode()
    {
        using var http = _server.RawHttp();
        var response = await http.PostAsJsonAsync("api/pair/start", new PairStartRequest("LAPTOP"), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("linked to a Pairnets account", (await response.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))!.Message);
    }

    [Fact]
    public async Task TheStatusSaysHowTheServerIsDoing()
    {
        var response = await SendAsync(Signed(HttpMethod.Get, "/api/relay/status"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await JsonOf(response);
        Assert.Equal(PairnetsInfo.ProductVersion, status.GetProperty("serverVersion").GetString());
        Assert.Equal(0, status.GetProperty("devices").GetInt32());
        Assert.True(status.GetProperty("freeBytes").ValueKind is JsonValueKind.Number or JsonValueKind.Null);
        Assert.Equal(0, status.GetProperty("dataBytes").GetInt64());

        // One computer and one 5-byte file later.
        var grant = _server.MintKey("LAPTOP");
        using var api = _server.Client("LAPTOP", grant.Key);
        using var content = new MemoryStream("hello"u8.ToArray());
        await api.UploadAsync("a.txt", "none", 1_700_000_000_000, content, null, CancellationToken.None);
        status = await JsonOf(await SendAsync(Signed(HttpMethod.Get, "/api/relay/status")));
        Assert.Equal(1, status.GetProperty("devices").GetInt32());
        Assert.Equal(5, status.GetProperty("dataBytes").GetInt64());
    }

    [Fact]
    public async Task AComputerTheServiceAddsSyncsIsListedAndIsRemovedLikeAnyOther()
    {
        // The service asks for a key when an app signs in with the owner's account.
        var added = await SendAsync(Signed(HttpMethod.Post, "/api/relay/devices",
            """{"name":"Laptop","system":"Windows 11","approvedBy":"m***@gmail.com"}"""));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var device = await JsonOf(added);
        var id = device.GetProperty("id").GetString()!;
        var key = device.GetProperty("key").GetString()!;
        Assert.Equal("Laptop", device.GetProperty("name").GetString());
        Assert.StartsWith(AuthStore.KeyPrefix, key);
        Assert.Equal("approved by m***@gmail.com on sync.example.com", Auth.GetDevice(id)!.ApprovedBy);

        // The key works like any computer's own key.
        using var laptop = _server.Client("whatever", key);
        var me = await laptop.GetMeAsync(CancellationToken.None);
        Assert.Equal((id, "Laptop", DeviceMe.KindDeviceKey), (me!.Id, me.Name, me.Kind));

        // Names are made unique by the nest.
        var second = await JsonOf(await SendAsync(Signed(HttpMethod.Post, "/api/relay/devices", """{"name":"Laptop","system":"macOS 15"}""")));
        Assert.Equal("Laptop (2)", second.GetProperty("name").GetString());
        Assert.Equal("approved on sync.example.com", Auth.GetDevice(second.GetProperty("id").GetString()!)!.ApprovedBy);

        // Listed for the account page, with when each was last seen (null: never yet).
        var list = (await JsonOf(await SendAsync(Signed(HttpMethod.Get, "/api/relay/devices")))).GetProperty("devices").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        var first = list.Single(d => d.GetProperty("id").GetString() == id);
        Assert.Equal(("Laptop", "Windows 11"), (first.GetProperty("name").GetString(), first.GetProperty("system").GetString()));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", first.GetProperty("createdAt").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", first.GetProperty("lastSeen").GetString());
        Assert.Equal(JsonValueKind.Null, list.Single(d => d.GetProperty("id").GetString() != id).GetProperty("lastSeen").ValueKind);

        // Removed on the account page: the app hears it, its push channel closes, its key stops working.
        var removedMessage = new TaskCompletionSource<(string Id, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(key))
            .Build();
        hub.On<string, string>(SyncHub.DeviceRemovedMethod, (removedId, name) => removedMessage.TrySetResult((removedId, name)));
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.StartAsync();

        var removed = await SendAsync(Signed(HttpMethod.Delete, "/api/relay/devices/" + id));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("""{"removed":true}""", await removed.Content.ReadAsStringAsync());

        Assert.Equal((id, "Laptop"), await removedMessage.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refused = await Assert.ThrowsAsync<PairnetsAuthException>(() => laptop.GetInfoAsync(CancellationToken.None));
        Assert.Equal(ErrorCodes.DeviceRemoved, refused.Code);
        Assert.DoesNotContain((await JsonOf(await SendAsync(Signed(HttpMethod.Get, "/api/relay/devices")))).GetProperty("devices").EnumerateArray(),
            d => d.GetProperty("id").GetString() == id);

        // Unknown (or already removed) computers.
        var again = await SendAsync(Signed(HttpMethod.Delete, "/api/relay/devices/" + id));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("""{"error":"not_found"}""", await again.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(Signed(HttpMethod.Delete, "/api/relay/devices/nobody"))).StatusCode);

        // Nothing secret reached the log: not the nest key, not the computer's key.
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(Key, StringComparison.Ordinal) || l.Contains(key, StringComparison.Ordinal));
        Assert.Contains(_server.Logs.Lines, l => l.Contains("Laptop joined the nest (approved by m***@gmail.com on sync.example.com)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAddRequestThatIsNotJsonIsRefused()
    {
        var response = await SendAsync(Signed(HttpMethod.Post, "/api/relay/devices", "not json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"bad_request"}""", await response.Content.ReadAsStringAsync());
        Assert.Empty(Auth.ListDevices());
    }

    [Fact]
    public async Task EveryCallThatIsNotProperlySignedIsRefusedTheSameWay()
    {
        const string json = """{"name":"Mallory"}""";
        await AssertRefusedAsync(new HttpRequestMessage(HttpMethod.Get, "api/relay/status"), "no signature");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices", json, nest: OtherNestId), "another nest");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices", json, key: KeyBytes.Select(b => (byte)~b).ToArray()), "another key");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices", json, time: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300), "too old");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices", json, time: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300), "from the future");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices", json, signedJson: """{"name":"Laptop"}"""), "another body");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices", json, signedPath: "/api/relay/status"), "another path");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices?x=1", json, signedPath: "/api/relay/devices"), "another query");
        await AssertRefusedAsync(Signed(HttpMethod.Post, "/api/relay/devices", json, signedMethod: "GET"), "another method");

        // A captured call sent again.
        var nonce = NewNonce();
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Signed(HttpMethod.Get, "/api/relay/status", time: time, nonce: nonce))).StatusCode);
        await AssertRefusedAsync(Signed(HttpMethod.Get, "/api/relay/status", time: time, nonce: nonce), "replayed");

        // A computer's own key is no way in either, and nothing under /api/relay is open, not even an unknown path.
        var grant = _server.MintKey("DESKTOP");
        var withKey = new HttpRequestMessage(HttpMethod.Get, "api/relay/devices");
        withKey.Headers.Add(PairnetsHeaders.Token, grant.Key);
        await AssertRefusedAsync(withKey, "a computer's key");
        var shared = new HttpRequestMessage(HttpMethod.Get, "api/relay/status");
        shared.Headers.Add(PairnetsHeaders.Token, _server.Token);
        await AssertRefusedAsync(shared, "the shared token");
        await AssertRefusedAsync(new HttpRequestMessage(HttpMethod.Get, "api/relay/whatever"), "an unknown path");
        await AssertRefusedAsync(new HttpRequestMessage(HttpMethod.Get, "API/RELAY/STATUS"), "another spelling");

        Assert.Single(Auth.ListDevices()); // only DESKTOP: no call above added anyone
        // The log says what failed, never with a header's value.
        Assert.Contains(_server.Logs.Lines, l => l.Contains("Refused a call to POST /api/relay/devices", StringComparison.Ordinal) && l.Contains("WrongNest", StringComparison.Ordinal));
        Assert.Contains(_server.Logs.Lines, l => l.Contains("Replayed", StringComparison.Ordinal));
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(nonce, StringComparison.Ordinal) || l.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSignatureIsNeverLogged()
    {
        var request = Signed(HttpMethod.Get, "/api/relay/status");
        var signature = request.Headers.GetValues(RelaySignature.SignatureHeader).Single();
        var nonce = request.Headers.GetValues(RelaySignature.NonceHeader).Single();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(request)).StatusCode);
        var bad = Signed(HttpMethod.Get, "/api/relay/status", nest: OtherNestId);
        await AssertRefusedAsync(bad, "another nest");
        var badSignature = bad.Headers.GetValues(RelaySignature.SignatureHeader).Single();

        foreach (var secret in new[] { signature, nonce, badSignature, Key })
            Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABodyOver4KiBIsRefusedBeforeAnythingElse()
    {
        var big = "{\"name\":\"Laptop\",\"pad\":\"" + new string('a', 4096) + "\"}";
        var response = await SendAsync(Signed(HttpMethod.Post, "/api/relay/devices", big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("""{"error":"too_large"}""", await response.Content.ReadAsStringAsync());
        Assert.Empty(Auth.ListDevices());

        // Exactly 4 KiB is fine.
        var name = "{\"name\":\"Laptop\",\"pad\":\"";
        var exact = name + new string('a', RelayAuthMiddleware.MaxBodyBytes - name.Length - 2) + "\"}";
        Assert.Equal(RelayAuthMiddleware.MaxBodyBytes, exact.Length);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(Signed(HttpMethod.Post, "/api/relay/devices", exact))).StatusCode);
    }

    [Fact]
    public async Task ANestThatIsNotLinkedRefusesEvenACorrectlyShapedCall()
    {
        await using var plain = await TestServer.StartAsync(config: new() { ["Sync:TrustProxyHeaders"] = "true" });
        using var http = plain.RawHttp();
        var response = await http.SendAsync(Signed(HttpMethod.Post, "/api/relay/devices", """{"name":"Laptop"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"bad_signature"}""", await response.Content.ReadAsStringAsync());
        Assert.Empty(plain.Services.GetRequiredService<AuthStore>().ListDevices());
    }

    // ------------------------------------------------------------------ the caller's address

    private static HttpRequestMessage BadTokenFrom(string? service, string? cloudflare)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "api/info");
        request.Headers.Add(PairnetsHeaders.Token, "wrong-token-wrong-token");
        if (service is not null)
            request.Headers.Add(ProxyClientAddressMiddleware.RelayClientHeader, service);
        if (cloudflare is not null)
            request.Headers.Add(ProxyClientAddressMiddleware.CloudflareHeader, cloudflare);
        return request;
    }

    [Fact]
    public async Task TheServicesClientAddressComesFirstInRelayMode()
    {
        using var http = _server.RawHttp();
        await http.SendAsync(BadTokenFrom("203.0.113.5", "198.51.100.9"));
        Assert.Contains(_server.Logs.Lines, l => l.Contains("from 203.0.113.5", StringComparison.Ordinal));
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains("198.51.100.9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALinkedNestTrustsItsLocalTunnelEvenWithoutTheProxySetting()
    {
        var config = Config();
        config.Remove("Sync:TrustProxyHeaders");
        await using var server = await TestServer.StartAsync(config: config);
        using var http = server.RawHttp();
        await http.SendAsync(BadTokenFrom("203.0.113.7", null));
        Assert.Contains(server.Logs.Lines, l => l.Contains("from 203.0.113.7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutRelayModeTheServicesHeaderMeansNothing()
    {
        await using var server = await TestServer.StartAsync(config: new() { ["Sync:TrustProxyHeaders"] = "true" });
        using var http = server.RawHttp();
        await http.SendAsync(BadTokenFrom("203.0.113.5", "198.51.100.9"));
        Assert.Contains(server.Logs.Lines, l => l.Contains("from 198.51.100.9", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Logs.Lines, l => l.Contains("203.0.113.5", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "127.0.0.1", "203.0.113.5", "198.51.100.9", "203.0.113.5")]
    [InlineData(true, "::1", "2001:db8::7", null, "2001:db8::7")]
    [InlineData(true, "127.0.0.1", null, "198.51.100.9", "198.51.100.9")] // the service always sends it; without it, Cloudflare's
    [InlineData(true, "127.0.0.1", "not-an-ip", "198.51.100.9", "127.0.0.1")]
    [InlineData(true, "192.0.2.10", "203.0.113.5", null, "192.0.2.10")] // only from this machine
    [InlineData(false, "127.0.0.1", "203.0.113.5", "198.51.100.9", "198.51.100.9")] // not linked: the header is ignored
    [InlineData(false, "127.0.0.1", "203.0.113.5", null, "127.0.0.1")]
    public async Task TheServicesAddressIsBelievedOnlyFromThisMachineInRelayMode(bool linked, string peer, string? service, string? cloudflare, string expected)
    {
        var options = linked ? new SyncOptions { RelayNestId = NestId, RelayKey = Key, TrustProxyHeaders = true } : new SyncOptions { TrustProxyHeaders = true };
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (service is not null)
            ctx.Request.Headers[ProxyClientAddressMiddleware.RelayClientHeader] = service;
        if (cloudflare is not null)
            ctx.Request.Headers[ProxyClientAddressMiddleware.CloudflareHeader] = cloudflare;
        IPAddress? seen = null;
        var middleware = new ProxyClientAddressMiddleware(c =>
        {
            seen = c.Connection.RemoteIpAddress;
            return Task.CompletedTask;
        }, options);
        await middleware.InvokeAsync(ctx);
        Assert.Equal(IPAddress.Parse(expected).MapToIPv6(), seen!.MapToIPv6());
    }
}
