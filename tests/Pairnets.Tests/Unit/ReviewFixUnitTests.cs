using System.Net;
using System.Net.Http.Json;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Server.Auth;
using Pairnets.Server.Auth.WebAuthn;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Things an independent security review found: links a server hands the apps, and flooded sign-in state.</summary>
public sealed class ReviewFixUnitTests
{
    private static readonly Uri Typed = new("https://nest.example.com/");

    [Theory]
    [InlineData("https://nest.example.com/link?code=ABCD-EFGH", "https://nest.example.com")]
    [InlineData("https://other.example.org:8443/anything", "https://other.example.org:8443")]
    [InlineData("http://other.example.com/link", null)] // plain http only to the very host that was typed
    [InlineData("\\\\evil\\share\\setup.exe", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("/System/Applications/Calculator.app", null)]
    [InlineData("C:\\Windows\\System32\\calc.exe", null)]
    [InlineData("ms-msdt:/id PCWDiagnostic", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://user:pass@nest.example.com/", null)]
    [InlineData("nest.example.com/link", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyWebAddressesAreEverHandedToTheSystem(string? given, string? expected) =>
        Assert.Equal(expected, Nest.SafeOrigin(given, Typed));

    [Fact]
    public void PlainHttpIsFineToTheVeryHostThatWasTyped() =>
        Assert.Equal("http://192.168.1.20:5075", Nest.SafeOrigin("http://192.168.1.20:5075/link", new Uri("http://192.168.1.20:5075/")));

    private sealed class FakeNest(string verifyUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/pair/start", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PairStartResponse("poll-secret", "KQ7M-4PXD", verifyUrl, 600, 1), options: PairnetsJson.Options),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PairPollResponse(PairPollResponse.Denied), options: PairnetsJson.Options),
            });
        }
    }

    [Theory]
    [InlineData("\\\\evil\\share\\setup.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("/Applications/Calculator.app")]
    public async Task ASignInNeverPassesAWeirdLinkToTheWindow(string verifyUrl)
    {
        var flow = new PairingFlow(Typed, "LAPTOP", handler: new FakeNest(verifyUrl), interval: TimeSpan.FromMilliseconds(10));
        string? shown = null;
        flow.Changed += state => shown ??= state.VerifyUrl;

        await flow.RunAsync(CancellationToken.None);

        Assert.Equal("https://nest.example.com/link?code=KQ7M-4PXD", shown); // rebuilt from the typed address and the code
    }

    [Fact]
    public async Task TheLinkKeepsTheNestsOwnNameButIsRebuilt()
    {
        var flow = new PairingFlow(Typed, "LAPTOP", handler: new FakeNest("https://nest.example.com/link?code=KQ7M-4PXD&x=%00"), interval: TimeSpan.FromMilliseconds(10));
        string? shown = null;
        flow.Changed += state => shown ??= state.VerifyUrl;

        await flow.RunAsync(CancellationToken.None);

        Assert.Equal("https://nest.example.com/link?code=KQ7M-4PXD", shown);
    }

    [Fact]
    public void AFloodOfNewChallengesDoesNotThrowOutOneInProgress()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var challenges = new WebAuthnChallenges(clock);
        for (var i = 0; i < 199; i++)
        {
            challenges.Create(WebAuthnChallenges.SignIn);
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }
        var (mine, expected) = challenges.Create(WebAuthnChallenges.SignIn);

        for (var i = 0; i < 60; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            challenges.Create(WebAuthnChallenges.SignIn);
        }

        Assert.Equal(expected, challenges.Consume(mine, WebAuthnChallenges.SignIn));
    }
}
