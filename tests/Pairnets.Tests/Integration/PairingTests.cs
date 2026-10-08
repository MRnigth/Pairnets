using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Adding a computer: it asks with a code, the owner approves on the website, it collects its own key.</summary>
public sealed class PairingTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartWithWebsiteAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private async Task<PairStartResponse> Start(string name = "LAPTOP-2")
    {
        using var anonymous = _server.RawHttp();
        var response = await anonymous.PostAsJsonAsync("api/pair/start", new PairStartRequest(name, "Windows", "1.0.80"), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PairStartResponse>(PairnetsJson.Options))!;
    }

    private async Task<PairPollResponse> Poll(string token)
    {
        using var anonymous = _server.RawHttp();
        var response = await anonymous.PostAsJsonAsync("api/pair/poll", new PairPollRequest(token), PairnetsJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairPollResponse>(PairnetsJson.Options))!;
    }

    private async Task<HttpClient> Owner()
    {
        var web = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);
        return web;
    }

    [Fact]
    public async Task ApprovedOnTheWebsiteTheComputerCollectsItsOwnKeyOnce()
    {
        var start = await Start();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", start.Code);
        Assert.Equal($"https://localhost/link?code={start.Code}", start.VerifyUrl);
        Assert.Equal(600, start.ExpiresInSeconds);
        Assert.Equal(PairPollResponse.Pending, (await Poll(start.PollToken)).Status);

        using var owner = await Owner();
        var view = (await owner.GetFromJsonAsync<WebEndpoints.PairView>($"web/api/pair/{start.Code.ToLowerInvariant().Replace("-", " ")}", PairnetsJson.Options))!;
        Assert.Equal(("LAPTOP-2", "Windows", "1.0.80", "pending"), (view.Name, view.System, view.AppVersion, view.Status));
        Assert.True(view.SameComputer); // the test asks and approves from the same address
        var approved = await owner.PostAsync($"web/api/pair/{start.Code}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var collected = await Poll(start.PollToken);
        Assert.Equal(PairPollResponse.Approved, collected.Status);
        Assert.StartsWith("pn_", collected.Key);
        Assert.Equal("LAPTOP-2", collected.Name);
        using var own = _server.Client("anything", collected.Key);
        Assert.Equal(collected.Id, (await own.GetMeAsync(CancellationToken.None))!.Id);
        Assert.Contains("approved by the owner", Auth.GetDevice(collected.Id!)!.ApprovedBy);

        Assert.Equal(PairPollResponse.Used, (await Poll(start.PollToken)).Status); // the key is never handed out twice
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"web/api/pair/{start.Code}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task ADeniedComputerIsToldSo()
    {
        var start = await Start();
        using var owner = await Owner();

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"web/api/pair/{start.Code}/deny", null)).StatusCode);

        Assert.Equal(PairPollResponse.Denied, (await Poll(start.PollToken)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"web/api/pair/{start.Code}/approve", null)).StatusCode);
        Assert.Empty(Auth.ListDevices());
    }

    [Fact]
    public async Task ApprovingNeedsTheOwnerNotJustAnyComputer()
    {
        var start = await Start();
        using var stranger = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsync($"web/api/pair/{start.Code}/approve", null)).StatusCode);
        using var shared = _server.RawHttp(_server.Token); // the sync token is not a website sign-in either
        Assert.Equal(HttpStatusCode.NotFound, (await shared.PostAsync($"web/api/pair/{start.Code}/approve", null)).StatusCode);
        Assert.Equal(PairPollResponse.Pending, (await Poll(start.PollToken)).Status);
    }

    [Fact]
    public async Task AComputerCanOnlyHaveAFewRequestsWaiting()
    {
        for (var i = 0; i < PairingEndpoints.MaxPendingPerAddress; i++)
            await Start("PC" + i);
        using var anonymous = _server.RawHttp();

        var refused = await anonymous.PostAsJsonAsync("api/pair/start", new PairStartRequest("PC9"), PairnetsJson.Options);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task UnknownOrMissingRequestsAreRefused()
    {
        using var anonymous = _server.RawHttp();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("api/pair/poll", new PairPollRequest("made-up"), PairnetsJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("api/pair/start", new PairStartRequest("  "), PairnetsJson.Options)).StatusCode);
        using var owner = await Owner();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("web/api/pair/BBBB-BBBB")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("web/api/pair/not-a-code")).StatusCode);
    }

    [Fact]
    public async Task TheOtherComputersHearThatSomeoneWantsToJoin()
    {
        var grant = _server.MintKey("DESKTOP");
        var heard = new TaskCompletionSource<(string Code, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decided = new TaskCompletionSource<(string Code, bool Approved)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(grant.Key))
            .Build();
        hub.On<string, string, string>(PairingEndpoints.PairRequestedMethod, (code, name, _) => heard.TrySetResult((code, name)));
        hub.On<string, bool>(PairingEndpoints.PairDecidedMethod, (code, approved) => decided.TrySetResult((code, approved)));
        await hub.StartAsync();

        var start = await Start();
        Assert.Equal((start.Code, "LAPTOP-2"), await heard.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        using var owner = await Owner();
        await owner.PostAsync($"web/api/pair/{start.Code}/approve", null);
        Assert.Equal((start.Code, true), await decided.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ThePendingRequestShowsOnTheDevicesPageAndTheNewComputerAfterwards()
    {
        var start = await Start("LAPTOP");
        using var owner = await Owner();
        var before = (await owner.GetFromJsonAsync<WebEndpoints.DevicesView>("web/api/devices", PairnetsJson.Options))!;
        Assert.Equal(start.Code, Assert.Single(before.Pending).Code);

        await owner.PostAsync($"web/api/pair/{start.Code}/approve", null);
        var key = (await Poll(start.PollToken)).Key!;
        using (var own = _server.Client("LAPTOP", key))
            await own.GetInfoAsync(CancellationToken.None);

        var after = (await owner.GetFromJsonAsync<WebEndpoints.DevicesView>("web/api/devices", PairnetsJson.Options))!;
        Assert.Empty(after.Pending);
        var laptop = Assert.Single(after.Devices);
        Assert.True(laptop.OwnKey);
        Assert.Equal("LAPTOP", laptop.Name);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"web/api/devices/{laptop.Id}")).StatusCode);
        using var removed = _server.Client("LAPTOP", key);
        Assert.Equal(ErrorCodes.DeviceRemoved, (await Assert.ThrowsAsync<Pairnets.Core.Api.PairnetsAuthException>(() => removed.GetInfoAsync(CancellationToken.None))).Code);
    }

    [Fact]
    public async Task HelloSaysComputersCanSignIn()
    {
        using var anonymous = _server.Client(token: "none");
        Assert.True((await anonymous.GetHelloAsync(CancellationToken.None))!.SignIn);
    }

    [Fact]
    public async Task PollSecretsAndKeysNeverReachTheLog()
    {
        var start = await Start();
        using var owner = await Owner();
        await owner.PostAsync($"web/api/pair/{start.Code}/approve", null);
        var key = (await Poll(start.PollToken)).Key!;

        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(start.PollToken, StringComparison.Ordinal) || l.Contains(key, StringComparison.Ordinal));
    }
}
