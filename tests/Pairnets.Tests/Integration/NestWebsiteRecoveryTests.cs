using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// The nest's website, through its real endpoints: a setup link resets a forgotten password, and a computer that
/// only ever used the old shared token can be forgotten on the Devices page.
/// </summary>
public sealed class NestWebsiteRecoveryTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartWithWebsiteAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private OwnerAuth Owner => _server.Services.GetRequiredService<OwnerAuth>();

    private static async Task<WebEndpoints.StateView> State(HttpClient web) =>
        (await web.GetFromJsonAsync<WebEndpoints.StateView>("web/api/state", PairnetsJson.Options))!;

    private async Task<HttpClient> OpenSetupLink()
    {
        var web = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);
        return web;
    }

    [Fact]
    public async Task ASetupLinkResetsAForgottenPassword()
    {
        // A password chosen a month ago, then forgotten.
        Owner.SetPassword("forgotten password 1");
        Auth.SetSetting(AuthStore.SettingPasswordSetMs, DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        using var setup = await OpenSetupLink();
        Assert.True((await State(setup)).CanResetPassword);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.PostAsJsonAsync("web/api/password", new { password = "brand new password 2" })).StatusCode);
        Assert.False((await State(setup)).CanResetPassword); // once per link

        using var browser = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("web/api/signin/password", new { password = "forgotten password 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsJsonAsync("web/api/signin/password", new { password = "brand new password 2" })).StatusCode);

        // A browser signed in with the password cannot replace it without it.
        Assert.False((await State(browser)).CanResetPassword);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("web/api/password", new { password = "third password 3" })).StatusCode);
        Assert.True(Owner.CheckPassword("brand new password 2"));
    }

    [Fact]
    public async Task AComputerOnlyOnTheSharedTokenCanBeForgotten()
    {
        using var shared = _server.Client("OLD-DESKTOP");
        await shared.GetInfoAsync(CancellationToken.None);
        _server.MintKey("LAPTOP");

        var cookies = new CookieContainer();
        using (var signIn = _server.WebBrowser(cookies))
            Assert.Equal(HttpStatusCode.NoContent, (await signIn.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);
        using var web = _server.WebBrowser(cookies);
        var before = (await web.GetFromJsonAsync<WebEndpoints.DevicesView>("web/api/devices", PairnetsJson.Options))!;
        Assert.False(before.Devices.Single(d => d.Name == "OLD-DESKTOP").OwnKey);

        // Same protections as the other website calls.
        using (var stranger = _server.WebBrowser())
            Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("web/api/devices/forget", new { name = "OLD-DESKTOP" })).StatusCode);
        using (var evil = _server.WebBrowser(cookies, origin: "https://evil.example"))
            Assert.Equal(HttpStatusCode.Forbidden, (await evil.PostAsJsonAsync("web/api/devices/forget", new { name = "OLD-DESKTOP" })).StatusCode);

        // A computer with its own key is removed with "Remove from Pairnets", not forgotten.
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsJsonAsync("web/api/devices/forget", new { name = "LAPTOP" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/devices/forget", new { name = "OLD-DESKTOP" })).StatusCode);

        var after = (await web.GetFromJsonAsync<WebEndpoints.DevicesView>("web/api/devices", PairnetsJson.Options))!;
        Assert.Equal(["LAPTOP"], after.Devices.Select(d => d.Name));
        var security = (await web.GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;
        Assert.Equal(0, security.DevicesOnSharedToken);
    }
}
