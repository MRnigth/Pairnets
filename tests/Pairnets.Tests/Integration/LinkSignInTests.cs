using System.Collections.Concurrent;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Sign in with a link (cloud/CONTRACT.md §6.9): the app shows a long one-time link, the browser that opens it signs in
/// and allows, and the long code it then shows, pasted into the app, hands over this computer's key.
/// </summary>
public sealed class LinkSignInTests : IAsyncLifetime
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

    private AccountSignIn Flow() => new("SORO-SERVER", service: _service.Url, interval: TimeSpan.FromMilliseconds(50), byLink: true);

    /// <summary>Starts a link sign-in and waits until it shows its link.</summary>
    private static async Task<(Task<AccountSignInState> Run, AccountSignInState Waiting, CancellationTokenSource Stop)> StartAsync(AccountSignIn flow)
    {
        var waiting = new TaskCompletionSource<AccountSignInState>(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.Changed += s =>
        {
            if (s.Stage == PairingStage.Waiting)
                waiting.TrySetResult(s);
        };
        var stop = new CancellationTokenSource();
        var run = flow.RunAsync(stop.Token);
        return (run, await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30)), stop);
    }

    [Fact]
    public async Task TheLinkIsShownAndOnlyThePastedCodeBringsTheKey()
    {
        var flow = Flow();
        var (run, waiting, stop) = await StartAsync(flow);
        using var _ = stop;

        Assert.True(waiting.ByLink);
        Assert.Null(waiting.Code);
        Assert.StartsWith($"{_service.Url.GetLeftPart(UriPartial.Authority)}/app/link?r=pnr_", waiting.VerifyUrl);
        Assert.Equal("code", _service.Started!.Value.GetProperty("mode").GetString());
        await Task.Delay(300);
        Assert.Equal(0, _service.Polls); // nothing is polled

        // A typo is caught here: nothing is sent.
        var typo = await flow.RedeemAsync(FakeSyncService.RightCode[..^1] + "9");
        Assert.Equal(new AccountSignIn.LinkRedeem(false, "That isn't the whole code, or it has a typo. Copy all of it from the browser and try again.", false), typo);
        Assert.Equal(new AccountSignIn.LinkRedeem(false, "Paste the code the browser shows after you press Allow.", false), await flow.RedeemAsync("  "));
        Assert.Empty(_service.Redeemed);

        // Not allowed yet, then a wrong code (with a check that adds up), then the right one pasted with spaces in small letters.
        _service.RedeemScript.Enqueue("not_allowed_yet");
        var early = await flow.RedeemAsync(FakeSyncService.RightCode);
        Assert.Equal("Open the link, sign in there and press Allow first. Then paste the code it shows.", early.Message);
        var wrong = await flow.RedeemAsync("0000-0000-0000-0000-0000-0000-0000-GMMR");
        Assert.Equal(new AccountSignIn.LinkRedeem(false, "That code doesn't match. Check you copied all of it. 4 tries left.", false), wrong);
        _service.RedeemScript.Enqueue("nest_offline");
        Assert.Equal("Allowed, but your nest isn't connected right now. Check that it is on, then press Continue again.", (await flow.RedeemAsync(FakeSyncService.RightCode)).Message);

        var right = await flow.RedeemAsync($" {FakeSyncService.RightCode.ToLowerInvariant().Replace('-', ' ')}\n");
        Assert.Equal(new AccountSignIn.LinkRedeem(true, null, false), right);
        var done = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Approved, done.Stage);
        Assert.Equal(_service.RelayUrl, done.Result!.ServerUrl);
        Assert.Equal("SORO-SERVER", done.Result.Device.Name);
        Assert.Equal(FakeSyncService.RightCode.Replace("-", "", StringComparison.Ordinal), _service.Redeemed.Last());
        Assert.Equal(_service.AppTokens.ToArray(), _service.LoggedOut.ToArray()); // the account token is not kept

        using var api = new PairnetsApiClient(done.Result.ServerUrl, done.Result.Device.Key, "SORO-SERVER", serviceUrl: _service.Url);
        Assert.Equal(done.Result.Device.Id, (await api.GetMeAsync(CancellationToken.None))!.Id);
    }

    [Theory]
    [InlineData("stopped", PairingStage.Denied, "This sign-in was stopped")]
    [InlineData("code_expired", PairingStage.Expired, "The code ran out")]
    [InlineData("expired", PairingStage.Expired, "The sign-in link ran out")]
    public async Task AStoppedOrRunOutSignInEndsAndSaysWhy(string answer, PairingStage stage, string says)
    {
        var flow = Flow();
        var (run, _, stop) = await StartAsync(flow);
        using var __ = stop;
        _service.RedeemScript.Enqueue(answer);
        var r = await flow.RedeemAsync(FakeSyncService.RightCode);
        Assert.True(r.Over);
        Assert.StartsWith(says, r.Message);
        var done = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(stage, done.Stage);
        Assert.StartsWith(says, done.Message);
        // Over is over: another try says so without asking.
        var count = _service.Redeemed.Count;
        Assert.True((await flow.RedeemAsync(FakeSyncService.RightCode)).Over);
        Assert.Equal(count, _service.Redeemed.Count);
    }

    [Fact]
    public async Task FiveWrongCodesStopIt()
    {
        var flow = Flow();
        var (run, _, stop) = await StartAsync(flow);
        using var __ = stop;
        for (var i = 0; i < 4; i++)
            Assert.False((await flow.RedeemAsync("0000-0000-0000-0000-0000-0000-0000-GMMR")).Over);
        var fifth = await flow.RedeemAsync("0000-0000-0000-0000-0000-0000-0000-GMMR");
        Assert.True(fifth.Over);
        Assert.Equal(PairingStage.Denied, (await run.WaitAsync(TimeSpan.FromSeconds(30))).Stage);
    }

    [Fact]
    public async Task CancellingStopsQuietly()
    {
        var flow = Flow();
        var (run, _, stop) = await StartAsync(flow);
        stop.Cancel();
        var done = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Waiting, done.Stage);
        stop.Dispose();
    }

    [Fact]
    public async Task ALinkThatIsNotTheServicesOwnPageIsRefused()
    {
        _service.VerificationLink = "https://evil.example.com/app/link?r=pnr_x";
        var done = await Flow().RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingStage.Failed, done.Stage);
        Assert.Equal("Pairnets answered with a link this app can't use. Update Pairnets, then try again.", done.Message);
    }

    [Fact]
    public async Task OnlyALinkSignInTakesACode()
    {
        var plain = new AccountSignIn("LAPTOP", service: _service.Url);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plain.RedeemAsync(FakeSyncService.RightCode));
    }
}
