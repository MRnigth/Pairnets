using System.Collections.Concurrent;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>"Continue with email / Google": signing a computer in with a Pairnets account (cloud/RELAY.md §6).</summary>
public sealed class AccountSignInTests : IAsyncLifetime
{
    private TestServer _nest = null!;
    private FakeSyncService _service = null!;

    public async Task InitializeAsync()
    {
        _nest = await TestServer.StartAsync();
        _service = await FakeSyncService.StartAsync(_nest);
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        await _nest.DisposeAsync();
    }

    private string Origin => _service.Url.GetLeftPart(UriPartial.Authority);

    private AccountSignIn Flow(string name = "LAPTOP", string? method = null) =>
        new(name, method, _service.Url, interval: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task SigningInWithTheAccountGivesTheServersRelayAddressAndThisComputersOwnKey()
    {
        foreach (var step in new[] { "pending", "slow_down", "pending", "trouble", "nest_offline", "nest_offline" })
            _service.PollScript.Enqueue(step);
        var flow = Flow(method: "email");
        var states = new ConcurrentQueue<AccountSignInState>();
        flow.Changed += states.Enqueue;

        var done = await flow.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(PairingStage.Approved, done.Stage);
        var waiting = states.First(s => s.Stage == PairingStage.Waiting);
        Assert.Equal(FakeSyncService.UserCode, waiting.Code);
        Assert.Equal($"{Origin}/app?code={FakeSyncService.UserCode}&method=email", waiting.VerifyUrl);
        Assert.StartsWith("code expires in 9:", waiting.ExpiresText(DateTimeOffset.UtcNow.AddSeconds(5)));
        Assert.True(flow.PollInterval > TimeSpan.FromMilliseconds(50), "asks less often after \"slow down\"");
        Assert.Contains(states, s => s.Message == "Pairnets is having trouble; still trying…");
        // Allowed while the server was off: the window says so, and the sign-in finishes by itself once it answers.
        Assert.Contains(states, s => s is { Stage: PairingStage.Waiting, ServerOffline: true, Message: AccountSignIn.ServerOfflineMessage });
        Assert.StartsWith("Allowed. Your server is not connected right now", AccountSignIn.ServerOfflineMessage);
        Assert.False(done.ServerOffline);
        Assert.Null(done.Message);

        var result = done.Result!;
        Assert.Equal(_service.RelayUrl, result.ServerUrl);
        Assert.Equal((FakeSyncService.NestId, "soro", FakeSyncService.Email, "LAPTOP"), (result.NestId, result.NestLabel, result.Email, result.Device.Name));
        var started = _service.Started!.Value;
        Assert.Equal("LAPTOP", started.GetProperty("name").GetString());
        Assert.Equal(PairnetsInfo.ProductVersion, started.GetProperty("appVersion").GetString());
        Assert.False(string.IsNullOrEmpty(started.GetProperty("system").GetString()));
        Assert.Equal(7, _service.Polls);

        // The account's token was signed out of straight away: the app keeps only this computer's key.
        Assert.Equal(_service.AppTokens.ToArray(), _service.LoggedOut.ToArray());
        Assert.Single(_service.LoggedOut);

        // That key works on the server, through its relay address.
        using var api = new PairnetsApiClient(result.ServerUrl, result.Device.Key, "LAPTOP", serviceUrl: _service.Url);
        Assert.True(api.IsRelay);
        Assert.Equal(result.Device.Id, (await api.GetMeAsync(CancellationToken.None))!.Id);
        Assert.Contains("GET /api/me", _service.Relayed);
    }

    [Fact]
    public async Task AnAccountWithNoNestYetWaitsForOneThenFinishesByItself()
    {
        // Allowed in the browser while the account had no nest: the service says so until the account adds one.
        foreach (var step in new[] { "pending", "no_nest", "no_nest", "no_nest" })
            _service.PollScript.Enqueue(step);
        var flow = Flow(method: "email");
        var states = new ConcurrentQueue<AccountSignInState>();
        flow.Changed += states.Enqueue;

        var done = await flow.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var waiting = states.First(s => s.NoNest);
        Assert.Equal(PairingStage.Waiting, waiting.Stage);
        Assert.Equal(FakeSyncService.Email, waiting.AccountEmail);
        Assert.Equal(AccountSignIn.NoNestMessage, waiting.Message);
        // The service holds such a sign-in for half an hour, and the window counts down from that.
        Assert.StartsWith("code expires in 29:", waiting.ExpiresText(DateTimeOffset.UtcNow.AddSeconds(5)));
        Assert.Equal(PairingStage.Approved, done.Stage);
        Assert.False(done.NoNest);
        Assert.Null(done.Message);
        Assert.Equal(_service.RelayUrl, done.Result!.ServerUrl);
    }

    [Fact]
    public async Task TurnedDownInTheBrowserOrTooLateTheSignInSaysSo()
    {
        _service.PollScript.Enqueue("pending");
        _service.AfterScript = "denied";
        var denied = await Flow().RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Denied, denied.Stage);
        Assert.Equal("This computer was turned down in the browser.", denied.Message);
        Assert.Null(denied.Result);

        _service.AfterScript = "expired";
        var expired = await Flow().RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Expired, expired.Stage);
        Assert.Equal("The code expired.", expired.Message);
        Assert.Empty(_service.LoggedOut); // no account token was ever handed out
    }

    [Fact]
    public async Task AnAnswerWithoutAKeyOrWithAnotherAddressIsRefusedAndItsTokenStillSignedOut()
    {
        _service.AfterScript = "approved-without-key"; // an older service: only the account's token
        var old = await Flow().RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Failed, old.Stage);
        Assert.Contains("did not hand out a key", old.Message);

        _service.AfterScript = "approved";
        _service.ServerUrlInAnswer = $"https://elsewhere.example/n/{FakeSyncService.NestId}/";
        var elsewhere = await Flow().RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Failed, elsewhere.Stage);
        Assert.Contains("cannot use", elsewhere.Message);
        Assert.Null(elsewhere.Result);

        _service.ServerUrlInAnswer = $"{_service.Url}n/nst_testnest000000000000000002/"; // another server than the one named
        var other = await Flow().RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Failed, other.Stage);

        Assert.Equal(3, _service.LoggedOut.Count);
        Assert.Equal(_service.AppTokens.ToArray(), _service.LoggedOut.ToArray());
    }

    [Fact]
    public async Task TheBrowserLinkStaysOnTheServiceAndCarriesTheButtonPressed()
    {
        _service.AfterScript = "pending";
        _service.VerificationLink = "https://evil.example/app?code=ABCD-EFGH";
        Assert.Equal($"{Origin}/app?code={FakeSyncService.UserCode}&method=google", (await WaitingAsync(Flow(method: "google"))).VerifyUrl);

        _service.VerificationLink = null;
        Assert.Equal($"{Origin}/app?code={FakeSyncService.UserCode}", (await WaitingAsync(Flow(method: "passkey"))).VerifyUrl);
        Assert.Equal($"{Origin}/app?code={FakeSyncService.UserCode}&method=email", (await WaitingAsync(Flow(method: "email"))).VerifyUrl);
    }

    /// <summary>Runs a sign-in until it shows its code, then cancels it (which stops it quietly where it was).</summary>
    private static async Task<AccountSignInState> WaitingAsync(AccountSignIn flow)
    {
        using var stop = new CancellationTokenSource();
        AccountSignInState? waiting = null;
        flow.Changed += s =>
        {
            if (s.Stage == PairingStage.Waiting)
            {
                waiting = s;
                stop.Cancel();
            }
        };
        var stopped = await flow.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Waiting, stopped.Stage);
        return waiting!;
    }

    [Fact]
    public async Task AServiceThatCannotBeReachedOrWillNotStartFails()
    {
        var unreachable = await new AccountSignIn("X", service: new Uri("http://127.0.0.1:9/")).RunAsync(CancellationToken.None);
        Assert.Equal(PairingStage.Failed, unreachable.Stage);
        Assert.StartsWith("Can't reach Pairnets", unreachable.Message);

        // The nest itself is not a sign-in service: its 404 is a refusal, not a crash.
        var wrong = await new AccountSignIn("X", service: _nest.Url).RunAsync(CancellationToken.None);
        Assert.Equal(PairingStage.Failed, wrong.Stage);
        Assert.Contains("could not start a sign-in", wrong.Message);
    }

    [Fact]
    public async Task TheNameIsCleanedUpTheWayTheServiceTakesIt()
    {
        // The service refuses names over 64 characters or with control characters: the app sends one it takes.
        var done = await Flow("\u0007" + new string('A', 100) + "  ").RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Approved, done.Stage);
        Assert.Equal(new string('A', 64), _service.Started!.Value.GetProperty("name").GetString());

        var settings = AccountSignIn.SettingsAfterSignIn(null, done.Result! with { Email = " you@example.com " }, "/data", true, "protected");
        Assert.Equal("you@example.com", settings.AccountEmail);
        Assert.Equal(_service.RelayUrl.ToString(), settings.ServerUrl);
    }
}
