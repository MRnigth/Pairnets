using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Server;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Running behind a Cloudflare Tunnel: real client addresses, and plain-words errors in the apps.</summary>
public class CloudflareTunnelTests
{
    private static HttpRequestMessage BadTokenRequest(string clientIp)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "api/info");
        req.Headers.Add("X-Sync-Token", "wrong-token-wrong-token");
        req.Headers.Add("CF-Connecting-IP", clientIp);
        return req;
    }

    [Fact]
    public async Task BehindTheTunnelOneStrangerCannotSlowDownEverybody()
    {
        await using var server = await TestServer.StartAsync(config: new() { ["Sync:TrustProxyHeaders"] = "true" });
        using var http = server.RawHttp();
        for (var i = 0; i < 7; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(BadTokenRequest("203.0.113.5"))).StatusCode);

        // Everything arrives from 127.0.0.1 (cloudflared), but the throttle counts per real client.
        var good = new HttpRequestMessage(HttpMethod.Get, "api/info");
        good.Headers.Add("X-Sync-Token", server.Token);
        good.Headers.Add("CF-Connecting-IP", "198.51.100.7");
        var watch = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(good)).StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        Assert.Contains(server.Logs.Lines, l => l.Contains("from 203.0.113.5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheSettingTheHeaderIsIgnored()
    {
        await using var server = await TestServer.StartAsync();
        using var http = server.RawHttp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(BadTokenRequest("203.0.113.5"))).StatusCode);
        Assert.DoesNotContain(server.Logs.Lines, l => l.Contains("203.0.113.5", StringComparison.Ordinal));
        Assert.Contains(server.Logs.Lines, l => l.Contains("from 127.0.0.1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("127.0.0.1", "203.0.113.5", null, "203.0.113.5")]
    [InlineData("::1", "2001:db8::7", null, "2001:db8::7")]
    [InlineData("::ffff:127.0.0.1", "203.0.113.5", null, "203.0.113.5")]
    [InlineData("127.0.0.1", null, "198.51.100.1, 203.0.113.9", "203.0.113.9")]
    [InlineData("127.0.0.1", "not-an-ip", null, "127.0.0.1")]
    [InlineData("127.0.0.1", null, null, "127.0.0.1")]
    [InlineData("192.0.2.10", "203.0.113.5", null, "192.0.2.10")] // only a proxy on this machine is believed
    public async Task TheClientAddressComesFromALocalProxyOnly(string peer, string? cloudflare, string? forwarded, string expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (cloudflare is not null)
            ctx.Request.Headers["CF-Connecting-IP"] = cloudflare;
        if (forwarded is not null)
            ctx.Request.Headers["X-Forwarded-For"] = forwarded;
        IPAddress? seen = null;
        var middleware = new ProxyClientAddressMiddleware(c =>
        {
            seen = c.Connection.RemoteIpAddress;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(ctx);
        Assert.Equal(IPAddress.Parse(expected).MapToIPv6(), seen!.MapToIPv6());
    }

    private const string NestName = "nest.example.com";

    /// <summary>A browser's request as cloudflared hands it on: plain HTTP from 127.0.0.1, the nest's name, the browser's address.</summary>
    private static HttpRequestMessage ThroughTheTunnel(HttpMethod method, string path, string? clientIp = "192.0.2.10", string? proto = "https")
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Host = NestName;
        if (clientIp is not null)
            req.Headers.Add("CF-Connecting-IP", clientIp);
        if (proto is not null)
            req.Headers.Add("X-Forwarded-Proto", proto);
        return req;
    }

    private static Task<TestServer> StartBehindTheTunnelAsync() => TestServer.StartAsync(config: new()
    {
        ["Sync:TrustProxyHeaders"] = "true",
        ["Sync:PublicUrl"] = "https://" + NestName,
    });

    private static HttpClient NoRedirects(TestServer server) =>
        new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = server.Url };

    [Fact]
    public async Task TheNestsWebsiteWorksBehindTheTunnel()
    {
        await using var server = await StartBehindTheTunnelAsync();
        using var http = NoRedirects(server);

        var state = await http.SendAsync(ThroughTheTunnel(HttpMethod.Get, "web/api/state"));
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Equal("application/json", state.Content.Headers.ContentType?.MediaType);
        var view = await state.Content.ReadFromJsonAsync<WebEndpoints.StateView>(PairnetsJson.Options);
        Assert.False(view!.SignedIn);
        Assert.Equal(NestName, view.NestName);

        // The pages are served, not redirected to themselves; so are their scripts and styles.
        foreach (var page in new[] { "setup", "signin" })
        {
            var resp = await http.SendAsync(ThroughTheTunnel(HttpMethod.Get, page));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        }
        var css = await http.SendAsync(ThroughTheTunnel(HttpMethod.Get, "assets/nest.css"));
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        var home = await http.SendAsync(ThroughTheTunnel(HttpMethod.Get, "/"));
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/setup", home.Headers.Location?.OriginalString);

        // A local proxy that sends no client address (Caddy without X-Forwarded-For) is still the proxy.
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ThroughTheTunnel(HttpMethod.Get, "setup", clientIp: null))).StatusCode);

        // Signing in from the nest's own pages works, and the cookie it sets is accepted back.
        var setup = ThroughTheTunnel(HttpMethod.Post, "web/api/setup");
        setup.Headers.Add("Origin", "https://" + NestName);
        setup.Content = JsonContent.Create(new { code = server.Services.GetRequiredService<AuthStore>().CreateSetupCode() });
        var signedIn = await http.SendAsync(setup);
        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);
        var cookie = Assert.Single(signedIn.Headers.GetValues("Set-Cookie")).Split(';')[0];
        Assert.StartsWith(OwnerAuth.CookieName + "=", cookie);
        var again = ThroughTheTunnel(HttpMethod.Get, "web/api/state");
        again.Headers.Add("Cookie", cookie);
        Assert.True((await (await http.SendAsync(again)).Content.ReadFromJsonAsync<WebEndpoints.StateView>(PairnetsJson.Options))!.SignedIn);

        // The server still sees the browser's own address, not cloudflared's 127.0.0.1.
        var wrong = ThroughTheTunnel(HttpMethod.Post, "web/api/setup");
        wrong.Headers.Add("Origin", "https://" + NestName);
        wrong.Content = JsonContent.Create(new { code = "not-a-real-code" });
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(wrong)).StatusCode);
        Assert.Contains(server.Logs.Lines, l => l.Contains("setup link was refused", StringComparison.Ordinal) && l.Contains("from 192.0.2.10", StringComparison.Ordinal));
        var join = ThroughTheTunnel(HttpMethod.Post, "api/pair/start");
        join.Content = JsonContent.Create(new PairStartRequest("LAPTOP-2"), options: PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(join)).StatusCode);
        Assert.Equal("LAPTOP-2", Assert.Single(server.Services.GetRequiredService<AuthStore>().ListPendingPairRequests("192.0.2.10")).Name);
    }

    [Fact]
    public async Task BehindTheTunnelPlainHttpOrAnotherNameIsNotTheWebsite()
    {
        await using var server = await StartBehindTheTunnelAsync();
        using var http = NoRedirects(server);

        // http:// through the tunnel is sent on to the https:// name; the API does not answer on it.
        var plain = await http.SendAsync(ThroughTheTunnel(HttpMethod.Get, "setup", proto: "http"));
        Assert.Equal(HttpStatusCode.Redirect, plain.StatusCode);
        Assert.Equal($"https://{NestName}/setup", plain.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(ThroughTheTunnel(HttpMethod.Get, "web/api/state", proto: null))).StatusCode);

        var other = ThroughTheTunnel(HttpMethod.Get, "web/api/state");
        other.Headers.Host = "other.example.com";
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(other)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.0.2.10", false)] // someone reaching the server directly cannot claim the tunnel's HTTPS
    [InlineData("203.0.113.5", false)]
    public async Task OnlyTheLocalProxyIsBelievedAboutHttps(string peer, bool secure)
    {
        using var dir = new TempDir("proxy");
        try
        {
            var options = new SyncOptions { TrustProxyHeaders = true, PublicUrl = "https://" + NestName };
            var owner = new OwnerAuth(new AuthStore(new ServerPaths(dir.Path)), options);
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString(NestName);
            ctx.Request.Headers["X-Forwarded-Proto"] = "https";
            ctx.Request.Headers["CF-Connecting-IP"] = "127.0.0.1"; // pretends to come from this machine
            ctx.Request.Headers.Origin = "https://" + NestName;
            bool? nest = null, sameOrigin = null;
            var middleware = new ProxyClientAddressMiddleware(c =>
            {
                nest = owner.IsNestRequest(c);
                sameOrigin = owner.IsSameOrigin(c);
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(ctx);

            Assert.Equal(secure, nest);
            Assert.Equal(secure, sameOrigin);
            Assert.Equal(secure, ProxyClientAddressMiddleware.CameThroughLocalProxy(ctx));
            // Without the setting the headers mean nothing, from anywhere.
            Assert.False(new OwnerAuth(owner.Store, new SyncOptions { PublicUrl = "https://" + NestName }).IsNestRequest(ctx));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static HttpResponseMessage CloudflarePage(HttpStatusCode status, string? mitigated = null)
    {
        var resp = new HttpResponseMessage(status) { Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html") };
        resp.Headers.Server.Add(new ProductInfoHeaderValue("cloudflare", null));
        resp.Headers.Add("CF-RAY", "8f00000000000000-AMS");
        if (mitigated is not null)
            resp.Headers.Add("cf-mitigated", mitigated);
        return resp;
    }

    [Fact]
    public void CloudflareErrorPagesAreExplainedInPlainWords()
    {
        Assert.Contains("pairnets-tunnel", PairnetsApiClient.DescribeCloudflareError(CloudflarePage((HttpStatusCode)530)));
        Assert.Contains("pairnets-tunnel", PairnetsApiClient.DescribeCloudflareError(CloudflarePage(HttpStatusCode.BadGateway)));
        Assert.Contains("524", PairnetsApiClient.DescribeCloudflareError(CloudflarePage((HttpStatusCode)524)));
        Assert.Contains("Bot Fight Mode", PairnetsApiClient.DescribeCloudflareError(CloudflarePage(HttpStatusCode.Forbidden, "challenge")));

        // Pairnets's own JSON errors relayed by Cloudflare, and errors without Cloudflare, are left alone.
        var relayed = CloudflarePage(HttpStatusCode.ServiceUnavailable);
        relayed.Content = new StringContent("{\"code\":\"busy\"}", Encoding.UTF8, "application/json");
        Assert.Null(PairnetsApiClient.DescribeCloudflareError(relayed));
        Assert.Null(PairnetsApiClient.DescribeCloudflareError(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        Assert.Null(PairnetsApiClient.DescribeCloudflareError(CloudflarePage(HttpStatusCode.OK)));
    }

    /// <summary>Plays Cloudflare in front of a Pairnets server; remembers whether the token was sent.</summary>
    private sealed class FakeCloudflare(HttpStatusCode healthStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(string Path, bool HadToken)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Seen.Add((path, request.Headers.Contains(PairnetsHeaders.Token)));
            HttpResponseMessage resp;
            if (path == "/api/health")
            {
                resp = healthStatus == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }
                    : CloudflarePage(healthStatus);
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"serverId\":\"s\",\"version\":1,\"apiVersion\":{PairnetsInfo.ApiVersion}}}", Encoding.UTF8, "application/json"),
                };
            }
            resp.Headers.Server.Add(new ProductInfoHeaderValue("cloudflare", null));
            resp.Headers.Add("CF-RAY", "8f00000000000000-AMS");
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task PlainHttpThroughCloudflareIsRefusedBeforeTheTokenIsSent()
    {
        var cloudflare = new FakeCloudflare();
        var test = await PairnetsApiClient.TestConnectionAsync("http://sync.example.com/", "a-token-a-token-a-token", "pc", cloudflare);
        Assert.Equal(ConnectionTestStatus.InvalidUrl, test.Status);
        Assert.Contains("https://", test.Message);
        Assert.Equal([("/api/health", false)], cloudflare.Seen);
    }

    [Fact]
    public async Task HttpsThroughCloudflareWorks()
    {
        var cloudflare = new FakeCloudflare();
        var test = await PairnetsApiClient.TestConnectionAsync("https://sync.example.com/", "a-token-a-token-a-token", "pc", cloudflare);
        Assert.Equal(ConnectionTestStatus.Ok, test.Status);
        Assert.Equal([("/api/health", false), ("/api/info", true)], cloudflare.Seen);
    }

    [Fact]
    public async Task ATunnelThatIsDownIsExplained()
    {
        var test = await PairnetsApiClient.TestConnectionAsync("https://sync.example.com/", "a-token-a-token-a-token", "pc", new FakeCloudflare((HttpStatusCode)530));
        Assert.Equal(ConnectionTestStatus.Unreachable, test.Status);
        Assert.Contains("sudo systemctl status pairnets-tunnel", test.Message);

        using var api = new PairnetsApiClient(new Uri("https://sync.example.com/"), "a-token-a-token-a-token", "pc", new FakeCloudflare((HttpStatusCode)530));
        Assert.False(await api.HealthAsync(default));
    }
}
