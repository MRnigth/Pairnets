using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server;
using Pairnets.Server.Auth.WebAuthn;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Passkeys on the nest's website, from setup link to signing in, with a software authenticator for a browser.</summary>
public sealed class PasskeyWebsiteTests : IAsyncLifetime
{
    private const string Origin = "https://localhost";

    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartWithWebsiteAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private async Task<HttpClient> SignedIn()
    {
        var web = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);
        return web;
    }

    private static async Task<(string Id, byte[] Challenge, JsonElement Options)> Options(HttpClient web, string path)
    {
        var response = await web.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var publicKey = json.GetProperty("publicKey");
        return (json.GetProperty("challengeId").GetString()!, WebAuthnVerifier.FromBase64Url(publicKey.GetProperty("challenge").GetString())!, publicKey);
    }

    private static string B(byte[] bytes) => WebAuthnVerifier.ToBase64Url(bytes);

    private static async Task<HttpResponseMessage> Register(HttpClient web, SoftwareAuthenticator key, string name = "Chrome on Test")
    {
        var (id, challenge, _) = await Options(web, "web/api/passkeys/register/options");
        var made = key.MakeCredential(challenge);
        return await web.PostAsJsonAsync("web/api/passkeys/register", new
        {
            challengeId = id, name, id = B(made.CredentialId), clientDataJson = B(made.ClientDataJson), attestationObject = B(made.AttestationObject),
        });
    }

    private static async Task<HttpResponseMessage> SignInWith(HttpClient web, SoftwareAuthenticator key, uint signCount = 0)
    {
        var (id, challenge, _) = await Options(web, "web/api/signin/passkey/options");
        var assertion = key.GetAssertion(challenge, signCount);
        return await web.PostAsJsonAsync("web/api/signin/passkey", new
        {
            challengeId = id, id = B(assertion.CredentialId), clientDataJson = B(assertion.ClientDataJson),
            authenticatorData = B(assertion.AuthenticatorData), signature = B(assertion.Signature),
        });
    }

    private async Task<WebEndpoints.StateView> State(HttpClient web) =>
        (await web.GetFromJsonAsync<WebEndpoints.StateView>("web/api/state", PairnetsJson.Options))!;

    [Fact]
    public async Task APasskeyIsAddedWhileSignedInAndThenSignsIn()
    {
        using var key = new SoftwareAuthenticator("localhost", Origin);
        using (var owner = await SignedIn())
        {
            var options = (await Options(owner, "web/api/passkeys/register/options")).Options;
            Assert.Equal("localhost", options.GetProperty("rp").GetProperty("id").GetString());
            Assert.Equal("none", options.GetProperty("attestation").GetString());
            Assert.Equal([-7, -257], options.GetProperty("pubKeyCredParams").EnumerateArray().Select(p => p.GetProperty("alg").GetInt32()));
            Assert.Equal(HttpStatusCode.NoContent, (await Register(owner, key)).StatusCode);
            var state = await State(owner);
            Assert.Equal(1, state.Methods.Passkeys);
            Assert.True(state.HasSignIn);
        }

        using var phone = _server.WebBrowser();
        Assert.False((await State(phone)).SignedIn);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInWith(phone, key)).StatusCode);
        var signedIn = await State(phone);
        Assert.True(signedIn.SignedIn);
        Assert.Equal("passkey", signedIn.SessionMethod);
        Assert.NotNull(Auth.ListPasskeys().Single().LastUsed);
    }

    [Fact]
    public async Task APasskeyAloneIsEnoughToSignInAndCannotBeRemovedAsTheLastWay()
    {
        using var key = new SoftwareAuthenticator("localhost", Origin);
        using var owner = await SignedIn();
        await Register(owner, key);

        var security = (await owner.GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;
        var passkey = Assert.Single(security.Passkeys);
        Assert.Equal("Chrome on Test", passkey.Name);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"web/api/passkeys/{passkey.Id}")).StatusCode);

        await owner.PostAsJsonAsync("web/api/password", new { password = "some long password" });
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"web/api/passkeys/{passkey.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync("web/api/password")).StatusCode); // now the password is the last way
    }

    [Fact]
    public async Task APasskeyCannotBeAddedWithoutSigningInOrTwice()
    {
        using var key = new SoftwareAuthenticator("localhost", Origin);
        using var stranger = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsync("web/api/passkeys/register/options", null)).StatusCode);

        using var owner = await SignedIn();
        Assert.Equal(HttpStatusCode.NoContent, (await Register(owner, key)).StatusCode);
        var again = await Register(owner, key);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task AChallengeWorksOnce()
    {
        using var key = new SoftwareAuthenticator("localhost", Origin);
        using var owner = await SignedIn();
        await Register(owner, key);
        using var phone = _server.WebBrowser();
        var (id, challenge, _) = await Options(phone, "web/api/signin/passkey/options");
        var assertion = key.GetAssertion(challenge);
        object Body() => new
        {
            challengeId = id, id = B(assertion.CredentialId), clientDataJson = B(assertion.ClientDataJson),
            authenticatorData = B(assertion.AuthenticatorData), signature = B(assertion.Signature),
        };

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync("web/api/signin/passkey", Body())).StatusCode);
        using var replay = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.BadRequest, (await replay.PostAsJsonAsync("web/api/signin/passkey", Body())).StatusCode);
        Assert.False((await State(replay)).SignedIn);
    }

    [Fact]
    public async Task AnotherWebsitesOrAnUnknownPasskeyDoesNotSignIn()
    {
        using var key = new SoftwareAuthenticator("localhost", Origin);
        using var owner = await SignedIn();
        await Register(owner, key);

        using var phishing = new SoftwareAuthenticator("localhost", "https://evil.example");
        var stolenId = key.CredentialId; // an attacker's page cannot make the real authenticator sign for the nest's origin
        using var browser = _server.WebBrowser();
        var (id, challenge, _) = await Options(browser, "web/api/signin/passkey/options");
        var fake = phishing.GetAssertion(challenge);
        var wrongOrigin = await browser.PostAsJsonAsync("web/api/signin/passkey", new
        {
            challengeId = id, id = B(stolenId), clientDataJson = B(fake.ClientDataJson), authenticatorData = B(fake.AuthenticatorData), signature = B(fake.Signature),
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongOrigin.StatusCode);

        using var unknown = new SoftwareAuthenticator("localhost", Origin);
        Assert.Equal(HttpStatusCode.BadRequest, (await SignInWith(browser, unknown)).StatusCode);
        Assert.False((await State(browser)).SignedIn);
    }

    [Fact]
    public async Task AClonedPasskeyIsRefusedAfterTheRealOneMovedOn()
    {
        using var key = new SoftwareAuthenticator("localhost", Origin);
        using var owner = await SignedIn();
        await Register(owner, key);
        using var one = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await SignInWith(one, key, signCount: 10)).StatusCode);

        using var clone = _server.WebBrowser();
        var refused = await SignInWith(clone, key, signCount: 4);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("cloned", (await refused.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))!.Message);
        Assert.False((await State(clone)).SignedIn);
    }

    [Fact]
    public async Task SigningInOptionsNeedAPasskeyAndTheNestsOwnName()
    {
        using var browser = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("web/api/signin/passkey/options", null)).StatusCode);
    }

    [Fact]
    public void ThePasskeyDomainDefaultsToTheNestAndMustBeAParentDomain()
    {
        var plain = new SyncOptions { Token = new string('x', 32), PublicUrl = "https://nest.pairnets.app" };
        Assert.Equal("nest.pairnets.app", plain.EffectiveRpId);
        Assert.Null(plain.ValidateForServe());

        Assert.Null(new SyncOptions { Token = new string('x', 32), PublicUrl = "https://nest.pairnets.app", PasskeyRpId = "pairnets.app" }.ValidateForServe());
        Assert.Contains("PasskeyRpId", new SyncOptions { Token = new string('x', 32), PublicUrl = "https://nest.pairnets.app", PasskeyRpId = "evil.example" }.ValidateForServe());
        Assert.Contains("PasskeyRpId", new SyncOptions { Token = new string('x', 32), PasskeyRpId = "pairnets.app" }.ValidateForServe());
        Assert.Contains("PasskeyRpId", new SyncOptions { Token = new string('x', 32), PublicUrl = "https://nest.pairnets.app", PasskeyRpId = "ts.app" }.ValidateForServe());
    }

    [Fact]
    public async Task SecretsAndPasskeyDataNeverReachTheLog()
    {
        using var key = new SoftwareAuthenticator("localhost", Origin);
        using var owner = await SignedIn();
        await Register(owner, key);
        using var browser = _server.WebBrowser();
        var (_, challenge, _) = await Options(browser, "web/api/signin/passkey/options");
        var assertion = key.GetAssertion(challenge);
        await SignInWith(browser, key, 3);

        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(B(assertion.Signature), StringComparison.Ordinal) || l.Contains(B(key.CredentialId), StringComparison.Ordinal));
    }
}
