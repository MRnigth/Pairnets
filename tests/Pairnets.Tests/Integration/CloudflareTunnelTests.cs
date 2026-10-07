using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Pairnets.Core;
using Pairnets.Core.Api;
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
