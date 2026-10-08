using System.Net;
using System.Net.Http.Json;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Sign in with Google, against a fake Google: connecting an account, signing in, and every way it can go wrong.</summary>
public sealed class GoogleSignInTests : IAsyncLifetime
{
    private const string Sub = "110169484474386276334";

    private FakeGoogle _google = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _google = await FakeGoogle.StartAsync();
        _server = await TestServer.StartWithWebsiteAsync(new()
        {
            ["Sync:GoogleClientId"] = FakeGoogle.ClientId,
            ["Sync:GoogleClientSecret"] = FakeGoogle.ClientSecret,
            ["Sync:GoogleAuthUrl"] = _google.AuthUrl,
            ["Sync:GoogleTokenUrl"] = _google.TokenUrl,
        });
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _google.DisposeAsync();
    }

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private async Task<HttpClient> SignedIn()
    {
        var web = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);
        return web;
    }

    private async Task<WebEndpoints.StateView> State(HttpClient web) =>
        (await web.GetFromJsonAsync<WebEndpoints.StateView>("web/api/state", PairnetsJson.Options))!;

    /// <summary>The parts of the redirect to Google that the nest sent the browser to.</summary>
    private sealed record Redirect(Uri Location, string State, string Nonce, string Challenge, System.Collections.Specialized.NameValueCollection Query);

    private async Task<Redirect> Start(HttpClient web, string path)
    {
        var response = await web.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(_google.AuthUrl, location.ToString());
        var query = HttpUtility.ParseQueryString(location.Query);
        _google.ExpectedChallenge = query["code_challenge"];
        return new Redirect(location, query["state"]!, query["nonce"]!, query["code_challenge"]!, query);
    }

    private async Task<string> Callback(HttpClient web, Redirect start, string code = "good-code") =>
        (await web.GetAsync($"auth/google/callback?state={start.State}&code={code}")).Headers.Location!.ToString();

    private void GoogleAnswers(Redirect start, string sub = Sub, string email = "you@gmail.com", string? nonce = null, string? audience = null,
        string? issuer = null, bool verified = true, long? expires = null) =>
        _google.IdTokenFor = _ => _google.IdToken(sub, email, nonce ?? start.Nonce, audience, issuer, verified, expires);

    [Fact]
    public async Task TheRedirectToGoogleCarriesEverythingASafeFlowNeeds()
    {
        using var owner = await SignedIn();

        var start = await Start(owner, "auth/google/start?purpose=connect");

        Assert.Equal(FakeGoogle.ClientId, start.Query["client_id"]);
        Assert.Equal("https://localhost/auth/google/callback", start.Query["redirect_uri"]);
        Assert.Equal("code", start.Query["response_type"]);
        Assert.Equal("openid email", start.Query["scope"]);
        Assert.Equal("S256", start.Query["code_challenge_method"]);
        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.True(start.State.Length >= 20 && start.Nonce.Length >= 20 && start.Challenge.Length >= 40);
        Assert.DoesNotContain(FakeGoogle.ClientSecret, start.Location.ToString()); // the secret only goes to Google's token endpoint
    }

    [Fact]
    public async Task ConnectingThenSigningInWithGoogle()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect);

        Assert.Equal("/security?connected=google", await Callback(owner, connect));

        Assert.Equal((Sub, "you@gmail.com"), Auth.GoogleAccount);
        Assert.True((await State(owner)).Methods.Google);
        Assert.Contains(_google.Requests, r => r.Contains("code_verifier=") && r.Contains("redirect_uri=https://localhost/auth/google/callback"));

        using var phone = _server.WebBrowser();
        var signIn = await Start(phone, "auth/google/start");
        GoogleAnswers(signIn);
        Assert.Equal("/devices", await Callback(phone, signIn));
        var state = await State(phone);
        Assert.True(state.SignedIn);
        Assert.Equal("google", state.SessionMethod);
    }

    [Fact]
    public async Task SigningInWithGoogleComesBackToThePageItLeft()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect);
        await Callback(owner, connect);

        // "Continue with Google" pressed in the app: the approval page sends its own address along.
        using var same = _server.WebBrowser();
        var signIn = await Start(same, "auth/google/start?next=" + Uri.EscapeDataString("/link?code=KQ7M-4PXD&method=google"));
        GoogleAnswers(signIn);
        Assert.Equal("/link?code=KQ7M-4PXD&method=google", await Callback(same, signIn));
        Assert.True((await State(same)).SignedIn);

        // Anything that is not a path on this site is dropped.
        using var other = _server.WebBrowser();
        var crooked = await Start(other, "auth/google/start?next=" + Uri.EscapeDataString("https://evil.example/"));
        GoogleAnswers(crooked);
        Assert.Equal("/devices", await Callback(other, crooked));
    }

    [Fact]
    public async Task ADifferentGoogleAccountDoesNotGetIn()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect);
        await Callback(owner, connect);

        using var stranger = _server.WebBrowser();
        var signIn = await Start(stranger, "auth/google/start");
        GoogleAnswers(signIn, sub: "999", email: "you@gmail.com"); // the same address is not enough: the account's own id counts

        Assert.Equal("/signin?error=google-mismatch", await Callback(stranger, signIn));
        Assert.False((await State(stranger)).SignedIn);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("unverified")]
    public async Task AnIdentityThatDoesNotCheckOutIsRefused(string problem)
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect,
            nonce: problem == "nonce" ? "someone-elses-nonce" : null,
            audience: problem == "audience" ? "another-client.apps.googleusercontent.com" : null,
            issuer: problem == "issuer" ? "https://evil.example" : null,
            verified: problem != "unverified",
            expires: problem == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds() : null);

        Assert.Equal("/signin?error=google-failed", await Callback(owner, connect));
        Assert.Null(Auth.GoogleAccount);
    }

    [Fact]
    public async Task AStateWorksOnceAndOnlyForTheBrowserThatStartedConnecting()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect);

        using var other = _server.WebBrowser(); // someone else's browser follows the link (login CSRF)
        Assert.Equal("/signin?error=google-expired", await Callback(other, connect));
        Assert.Null(Auth.GoogleAccount);

        Assert.Equal("/signin?error=google-expired", await Callback(owner, connect)); // the state was used up
    }

    [Fact]
    public async Task CancellingAtGoogleOrAnUnknownStateStopsCleanly()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        var denied = await owner.GetAsync($"auth/google/callback?state={connect.State}&error=access_denied");
        Assert.Equal("/signin?error=google-denied", denied.Headers.Location!.ToString());

        var unknown = await owner.GetAsync("auth/google/callback?state=made-up&code=x");
        Assert.Equal("/signin?error=google-expired", unknown.Headers.Location!.ToString());
        Assert.Null(Auth.GoogleAccount);
    }

    [Fact]
    public async Task APkceVerifierThatDoesNotMatchIsRefusedByGoogle()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect);
        _google.ExpectedChallenge = "a-different-challenge";

        Assert.Equal("/signin?error=google-failed", await Callback(owner, connect));
    }

    [Fact]
    public async Task GoogleCannotBeUsedBeforeItIsConnectedOrWhenNotSetUp()
    {
        using var stranger = _server.WebBrowser();
        Assert.Equal("/signin?error=google-none", (await stranger.GetAsync("auth/google/start")).Headers.Location!.ToString());
        Assert.Equal("/signin", (await stranger.GetAsync("auth/google/start?purpose=connect")).Headers.Location!.ToString()); // connecting needs a signed-in browser

        await using var plain = await TestServer.StartWithWebsiteAsync();
        using var web = plain.WebBrowser();
        Assert.Equal("/signin?error=google-none", (await web.GetAsync("auth/google/start")).Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await web.GetAsync("auth/google/callback?state=x&code=y")).StatusCode);
    }

    [Fact]
    public async Task OnlyOnTheNestsOwnName()
    {
        using var plain = _server.RawHttp();
        Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync("auth/google/start")).StatusCode);
    }

    [Fact]
    public async Task TheLastWayInCannotBeDisconnected()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect);
        await Callback(owner, connect);

        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync("web/api/google")).StatusCode);

        await owner.PostAsJsonAsync("web/api/password", new { password = "some long password" });
        var security = (await owner.GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;
        Assert.True(security.GoogleAvailable);
        Assert.Equal("you@gmail.com", security.GoogleEmail);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("web/api/google")).StatusCode);
        Assert.Null(Auth.GoogleAccount);
    }

    [Fact]
    public async Task TheClientSecretAndTokensNeverReachTheLog()
    {
        using var owner = await SignedIn();
        var connect = await Start(owner, "auth/google/start?purpose=connect");
        GoogleAnswers(connect);
        await Callback(owner, connect);

        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(FakeGoogle.ClientSecret, StringComparison.Ordinal) || l.Contains(connect.State, StringComparison.Ordinal)
            || l.Contains("code=good-code", StringComparison.Ordinal));
    }
}
