using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Cli;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>The nest's website: setup link, password, sessions, security settings and its protections.</summary>
public sealed class NestWebsiteTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartWithWebsiteAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private static async Task<string?> Message(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))?.Message;

    private async Task<WebEndpoints.StateView> State(HttpClient web) =>
        (await web.GetFromJsonAsync<WebEndpoints.StateView>("web/api/state", PairnetsJson.Options))!;

    /// <summary>A browser signed in with a setup link (and, if asked, with a password set).</summary>
    private async Task<HttpClient> SignedIn(string? password = null)
    {
        var web = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);
        if (password is not null)
            Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/password", new { password })).StatusCode);
        return web;
    }

    [Fact]
    public async Task ASetupLinkSignsInOnceWithASafeCookie()
    {
        var cookies = new CookieContainer();
        using var web = _server.WebBrowser(cookies);
        Assert.False((await State(web)).HasSignIn);
        var code = Auth.CreateSetupCode();

        var first = await web.PostAsJsonAsync("web/api/setup", new { code });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var setCookie = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-pn_session=", setCookie);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.True((await State(web)).SignedIn);

        using var other = _server.WebBrowser();
        var again = await other.PostAsJsonAsync("web/api/setup", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("owner-link", await Message(again));
    }

    [Fact]
    public async Task APasswordSignsInAndAWrongOneDoesNot()
    {
        using (var setup = await SignedIn("correct horse battery"))
            Assert.True((await State(setup)).Methods.Password);

        using var web = _server.WebBrowser();
        var wrong = await web.PostAsJsonAsync("web/api/signin/password", new { password = "wrong password!" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("Wrong password.", await Message(wrong));
        Assert.False((await State(web)).SignedIn);

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/signin/password", new { password = "correct horse battery" })).StatusCode);
        var state = await State(web);
        Assert.True(state.SignedIn);
        Assert.Equal("password", state.SessionMethod);

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("web/api/signout", null)).StatusCode);
        Assert.False((await State(web)).SignedIn);
    }

    [Fact]
    public async Task PasswordsMustBeLongAndChangingOneNeedsTheOldOne()
    {
        using var web = await SignedIn();
        var tooShort = await web.PostAsJsonAsync("web/api/password", new { password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("10 characters", await Message(tooShort));

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/password", new { password = "first password 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsJsonAsync("web/api/password", new { password = "second password 2" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/password", new { password = "second password 2", current = "first password 1" })).StatusCode);
        Assert.True(_server.Services.GetRequiredService<OwnerAuth>().CheckPassword("second password 2"));

        // The only way in cannot be removed.
        Assert.Equal(HttpStatusCode.Conflict, (await web.DeleteAsync("web/api/password")).StatusCode);
    }

    [Fact]
    public async Task OtherSitesCannotActWithTheOwnersCookie()
    {
        var cookies = new CookieContainer();
        using (var signedIn = _server.WebBrowser(cookies))
            Assert.Equal(HttpStatusCode.NoContent, (await signedIn.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);

        using var evil = _server.WebBrowser(cookies, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await evil.PostAsJsonAsync("web/api/password", new { password = "attacker password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await evil.PostAsJsonAsync("web/api/shared-token", new { allowed = false })).StatusCode);
        using var noOrigin = _server.WebBrowser(cookies, origin: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await noOrigin.PostAsync("web/api/signout", null)).StatusCode);
        Assert.Null(Auth.PasswordHash);
        Assert.True(Auth.AllowSharedToken);
    }

    [Fact]
    public async Task EverythingButSigningInNeedsASignedInBrowser()
    {
        using var web = _server.WebBrowser();
        foreach (var (method, path) in new[]
        {
            ("GET", "web/api/devices"), ("GET", "web/api/security"), ("POST", "web/api/password"), ("DELETE", "web/api/password"),
            ("POST", "web/api/shared-token"), ("DELETE", "web/api/devices/x"), ("PATCH", "web/api/devices/x"), ("GET", "web/api/pair/BBBBBBBB"),
            ("POST", "web/api/pair/BBBBBBBB/approve"), ("POST", "web/api/pair/BBBBBBBB/deny"), ("DELETE", "web/api/sessions/x"), ("POST", "web/api/signout"),
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
            Assert.True((await web.SendAsync(request)).StatusCode == HttpStatusCode.Unauthorized, $"{method} {path}");
        }
    }

    [Fact]
    public async Task TheWebsiteOnlyLivesOnTheNestsOwnName()
    {
        using var plain = _server.RawHttp();
        Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync("web/api/state")).StatusCode);
        using var noRedirect = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = _server.Url };
        var page = await noRedirect.GetAsync("signin");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("https://localhost/signin", page.Headers.Location?.ToString());

        await using var noName = await TestServer.StartAsync();
        using var bare = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = noName.Url };
        var explained = await bare.GetAsync("signin");
        Assert.Equal(HttpStatusCode.NotFound, explained.StatusCode);
        Assert.Contains("install.sh --public-url https://", await explained.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PagesComeWithAStrictContentSecurityPolicy()
    {
        using var web = _server.WebBrowser();
        foreach (var path in new[] { "signin", "setup", "link", "devices", "security" })
        {
            var response = await web.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(WebUi.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains($"data-page=\"{path}\"", html);
            Assert.DoesNotContain("<script>", html); // no inline scripts
            Assert.DoesNotContain(" style=", html); // no inline styles
        }
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("assets/nest.js")).StatusCode);
        Assert.Equal("image/svg+xml", (await web.GetAsync("assets/logo.svg")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync("assets/missing.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync("assets/..%2F..%2Fappsettings.json")).StatusCode);
    }

    [Fact]
    public async Task TheApprovalPageSendsYouBackToTheApp()
    {
        using var web = _server.WebBrowser();
        var js = await web.GetStringAsync("assets/nest.js");
        // After Allow on the computer that is signing in, the page opens pairnets:// to bring the
        // app back to the front. The link carries nothing: the key travels only through the poll.
        Assert.Contains("pairnets://signed-in", js);
        Assert.Contains("sameComputer", js);
    }

    [Fact]
    public async Task TheFrontDoorSendsYouWhereYouBelong()
    {
        using var web = _server.WebBrowser();
        Assert.Equal("/setup", (await web.GetAsync("")).Headers.Location?.ToString());
        Auth.SetPasswordHash("x");
        Assert.Equal("/signin", (await web.GetAsync("")).Headers.Location?.ToString());
        Auth.SetPasswordHash(null);
        using var signedIn = await SignedIn();
        Assert.Equal("/devices", (await signedIn.GetAsync("")).Headers.Location?.ToString());
    }

    [Fact]
    public async Task SessionsAreListedAndCanBeSignedOutFromAnotherBrowser()
    {
        using var phone = await SignedIn("phone password 1");
        using var laptop = _server.WebBrowser();
        await laptop.PostAsJsonAsync("web/api/signin/password", new { password = "phone password 1" });

        var security = (await laptop.GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;
        Assert.Equal(2, security.Sessions.Count);
        var other = security.Sessions.Single(s => !s.Current);
        Assert.Equal("setup link", other.Method);

        Assert.Equal(HttpStatusCode.NoContent, (await laptop.DeleteAsync($"web/api/sessions/{other.Id}")).StatusCode);
        Assert.False((await State(phone)).SignedIn);
        Assert.True((await State(laptop)).SignedIn);
    }

    [Fact]
    public async Task TheSharedTokenCanBeTurnedOffHere()
    {
        using var web = await SignedIn();
        using var shared = _server.Client("DESKTOP");
        await shared.GetInfoAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/shared-token", new { allowed = false })).StatusCode);

        var refused = await Assert.ThrowsAsync<Pairnets.Core.Api.PairnetsAuthException>(() => shared.GetInfoAsync(CancellationToken.None));
        Assert.Equal(ErrorCodes.SharedTokenOff, refused.Code);
        var security = (await web.GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;
        Assert.False(security.SharedTokenAllowed);
        Assert.Equal(1, security.DevicesOnSharedToken);
    }

    [Fact]
    public async Task OwnerLinkPrintsAWorkingOneTimeLink()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await CliCommands.RunAsync(["owner-link", "--data-dir", _server.DataDir], output, error);

        Assert.True(code == 0, error.ToString());
        var link = output.ToString().Trim();
        Assert.StartsWith("https://localhost/setup#code=", link);
        using var web = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = link[(link.IndexOf('=') + 1)..] })).StatusCode);

        // --if-new prints nothing once a way to sign in exists.
        Auth.SetPasswordHash("x");
        var quiet = new StringWriter();
        Assert.Equal(0, await CliCommands.RunAsync(["owner-link", "--if-new", "--data-dir", _server.DataDir], quiet, error));
        Assert.Equal(string.Empty, quiet.ToString());
    }

    [Fact]
    public async Task SecretsNeverReachTheLog()
    {
        var setupCode = Auth.CreateSetupCode();
        using var web = _server.WebBrowser();
        await web.PostAsJsonAsync("web/api/setup", new { code = setupCode });
        await web.PostAsJsonAsync("web/api/password", new { password = "my secret passphrase" });
        await web.PostAsJsonAsync("web/api/signin/password", new { password = "my wrong passphrase" });

        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(setupCode, StringComparison.Ordinal) || l.Contains("passphrase", StringComparison.Ordinal));
    }
}
