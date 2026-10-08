using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Things an independent security review found; each test fails on the code as it was.</summary>
public sealed class ReviewFixTests
{
    private static readonly Dictionary<string, string?> FastChecks = new() { ["Sync:ConnectionCheckInterval"] = "00:00:01" };

    private static async Task<(HubConnection Hub, Task Closed)> OpenHubAsync(TestServer server, string accessToken)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken))
            .Build();
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.StartAsync();
        return (hub, closed.Task);
    }

    [Fact]
    public async Task AComputerRemovedFromTheCommandLineLosesItsPushConnection()
    {
        await using var server = await TestServer.StartAsync(config: FastChecks);
        var laptop = server.MintKey("LAPTOP");
        var (hub, closed) = await OpenHubAsync(server, laptop.Key);
        await using var _ = hub;

        // pairnets-server devices remove runs in its own process: it only touches auth.db, not this server's memory.
        Assert.True(new AuthStore(server.Paths).RemoveDevice(laptop.Id));

        await closed.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
        using var own = server.Client("LAPTOP", laptop.Key);
        await Assert.ThrowsAsync<PairnetsAuthException>(() => own.GetInfoAsync(CancellationToken.None)); // HTTP is locked out now too
    }

    [Fact]
    public async Task TurningOffTheSharedTokenClosesConnectionsThatUseIt()
    {
        await using var server = await TestServer.StartAsync(config: FastChecks);
        var grant = server.MintKey("LAPTOP");
        var (oldHub, oldClosed) = await OpenHubAsync(server, server.Token);
        var (ownHub, _) = await OpenHubAsync(server, grant.Key);
        await using var _1 = oldHub;
        await using var _2 = ownHub;

        new AuthStore(server.Paths).AllowSharedToken = false; // from another process

        await oldClosed.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Connected, ownHub.State);
    }

    [Fact]
    public async Task ParallelWrongPasswordGuessesAreSlowedDownTogether()
    {
        await using var server = await TestServer.StartWithWebsiteAsync();
        var auth = server.Services.GetRequiredService<AuthStore>();
        using var owner = server.WebBrowser();
        await owner.PostAsJsonAsync("web/api/setup", new { code = auth.CreateSetupCode() });
        await owner.PostAsJsonAsync("web/api/password", new { password = "correct horse battery" });
        var browsers = Enumerable.Range(0, 7).Select(_ => server.WebBrowser()).ToList();

        var started = DateTimeOffset.UtcNow;
        var answers = await Task.WhenAll(browsers.Select(b => b.PostAsJsonAsync("web/api/signin/password", new { password = "wrong password!" })));
        var took = DateTimeOffset.UtcNow - started;

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.BadRequest, a.StatusCode));
        // Five wrong guesses are free, then the waits grow (1 s, 2 s, ...). Sent at once, they used to all skip the wait.
        Assert.True(took > TimeSpan.FromSeconds(2.5), $"7 parallel wrong guesses took only {took.TotalSeconds:0.0} s");
        browsers.ForEach(b => b.Dispose());
    }
}
