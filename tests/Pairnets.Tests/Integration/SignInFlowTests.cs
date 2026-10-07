using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>The apps' "Sign in with your browser": finding the nest and the ask → approve → key flow.</summary>
public sealed class SignInFlowTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartWithWebsiteAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>The test certificate is self-signed; the real nest has a Let's Encrypt one.</summary>
    private static SocketsHttpHandler Trusting()
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    private string NestAddress => $"localhost:{_server.HttpsUrl!.Port}";

    private async Task<HttpClient> Owner()
    {
        var web = _server.WebBrowser();
        var code = _server.Services.GetRequiredService<AuthStore>().CreateSetupCode();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code })).StatusCode);
        return web;
    }

    [Theory]
    [InlineData("nest.pairnets.app", "https://nest.pairnets.app/")]
    [InlineData("  nest.pairnets.app/  ", "https://nest.pairnets.app/")]
    [InlineData("nest.pairnets.app:8443", "https://nest.pairnets.app:8443/")]
    [InlineData("https://nest.pairnets.app", "https://nest.pairnets.app/")]
    [InlineData("192.0.2.4", "http://192.0.2.4:5075/")]
    [InlineData("192.0.2.4:6000", "http://192.0.2.4:6000/")]
    [InlineData("http://192.0.2.4:5075/", "http://192.0.2.4:5075/")]
    [InlineData("localhost:8443", "https://localhost:8443/")]
    [InlineData("nest", null)]
    [InlineData("not an address", null)]
    [InlineData("", null)]
    [InlineData("ftp://nest.pairnets.app", null)]
    public void TypedAddressesBecomeUrls(string typed, string? expected) => Assert.Equal(expected, Nest.ParseAddress(typed)?.ToString());

    [Fact]
    public async Task TheCheckFindsANestThatLetsComputersSignIn()
    {
        var found = await Nest.CheckAsync(NestAddress, Trusting());
        Assert.Equal(NestCheckStatus.Found, found.Status);
        Assert.True(found.CanSignIn);
        Assert.StartsWith("Found it", found.Message);

        await using var noName = await TestServer.StartAsync();
        Assert.Equal(NestCheckStatus.NoSignIn, (await Nest.CheckAsync(noName.Url.ToString())).Status);
        Assert.Equal(NestCheckStatus.Unreachable, (await Nest.CheckAsync("http://127.0.0.1:9/")).Status);
        Assert.Equal(NestCheckStatus.Invalid, (await Nest.CheckAsync("not an address")).Status);
        Assert.Equal(NestCheckStatus.Empty, (await Nest.CheckAsync("  ")).Status);
    }

    [Fact]
    public async Task ApprovedOnTheNestTheFlowEndsWithThisComputersKey()
    {
        var flow = new PairingFlow(Nest.ParseAddress(NestAddress)!, "LAPTOP-2", handler: Trusting(), interval: TimeSpan.FromMilliseconds(100));
        var waiting = new TaskCompletionSource<PairingState>(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.Changed += s =>
        {
            if (s.Stage == PairingStage.Waiting)
                waiting.TrySetResult(s);
        };
        var run = flow.RunAsync(CancellationToken.None);

        var shown = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", shown.Code);
        Assert.Equal($"https://localhost/link?code={shown.Code}", shown.VerifyUrl);
        Assert.StartsWith("code expires in 9:", shown.ExpiresText(DateTimeOffset.UtcNow.AddSeconds(5)));
        using var owner = await Owner();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"web/api/pair/{shown.Code}/approve", null)).StatusCode);

        var done = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PairingStage.Approved, done.Stage);
        Assert.Equal("LAPTOP-2", done.Grant!.Name);
        using var own = _server.Client("x", done.Grant.Key);
        Assert.Equal(done.Grant.Id, (await own.GetMeAsync(CancellationToken.None))!.Id);

        var settings = Nest.SettingsAfterSignIn(new() { ExtraIgnore = ["*.bak"], ParallelTransfers = 8 }, Nest.ParseAddress(NestAddress)!, done.Grant, "/data/Work", true, "protected");
        Assert.Equal((done.Grant.Id, "LAPTOP-2", "/data/Work", "protected", true, true), (settings.DeviceId, settings.DeviceName, settings.Folder, settings.ProtectedToken, settings.FirstRunCompleted, settings.HasOwnKey));
        Assert.Equal(8, settings.ParallelTransfers); // other settings carry over
        Assert.Equal(["*.bak"], settings.ExtraIgnore);
        Assert.True(settings.IsComplete);
    }

    [Fact]
    public async Task TurnedDownTheFlowSaysSo()
    {
        var flow = new PairingFlow(Nest.ParseAddress(NestAddress)!, "STRANGER", handler: Trusting(), interval: TimeSpan.FromMilliseconds(100));
        var waiting = new TaskCompletionSource<PairingState>(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.Changed += s =>
        {
            if (s.Stage == PairingStage.Waiting)
                waiting.TrySetResult(s);
        };
        var run = flow.RunAsync(CancellationToken.None);
        var shown = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var owner = await Owner();
        await owner.PostAsync($"web/api/pair/{shown.Code}/deny", null);

        var done = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PairingStage.Denied, done.Stage);
        Assert.Null(done.Grant);
    }

    [Fact]
    public async Task YourOtherComputersHearThatOneWantsToJoin()
    {
        using var folder = new TempDir("join-folder");
        using var stateBase = new TempDir("join-state");
        await using var session = ClientSession.Start(new Pairnets.Core.Settings.ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused",
            Folder = folder.Path,
            DeviceName = "DESKTOP",
            FirstRunCompleted = true,
        }, _server.Token, new Pairnets.Core.Sync.PermanentDeleteTrash(), stateBaseDir: stateBase.Path,
            runnerOptions: o => new Pairnets.Core.Sync.RunnerOptions { ServerUrl = o.ServerUrl, Token = o.Token, DeviceId = o.DeviceId, PeriodicInterval = TimeSpan.FromHours(1) });
        var heard = new TaskCompletionSource<JoinRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.JoinRequested += r => heard.TrySetResult(r);
        await WaitUntil(() => session.Status.Status == Pairnets.Core.Sync.RunnerStatus.Idle);
        await session.CheckAccountAsync(await session.Api.GetInfoAsync(CancellationToken.None));
        // The session also runs an account check on start-up; if that one is still in flight the explicit
        // call above is a no-op (guarded), so wait for whichever finishes to record the nest's address.
        await WaitUntil(() => session.Status.NestUrl == "https://localhost");
        Assert.Equal("https://localhost", session.Status.NestUrl);

        // The push channel connects in the background: ask (at most 3 may wait per address) until it hears one.
        for (var i = 0; i < PairingEndpoints.MaxPendingPerAddress && !heard.Task.IsCompleted; i++)
        {
            using var anonymous = _server.RawHttp();
            var start = await anonymous.PostAsJsonAsync("api/pair/start", new PairStartRequest("LAPTOP-" + i, "Windows"), PairnetsJson.Options);
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            await Task.WhenAny(heard.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        }
        var request = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal($"{request.Name} (Windows) wants to join", request.Title);
        Assert.Contains(session.Status.JoinRequests, r => r.Code == request.Code);
        Assert.Equal($"https://localhost/link?code={request.Code}", session.Status.ReviewUrl(request));

        using var owner = await Owner();
        await owner.PostAsync($"web/api/pair/{request.Code}/deny", null);
        await WaitUntil(() => session.Status.JoinRequests.All(r => r.Code != request.Code));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task CancellingStopsQuietlyAndAnUnreachableNestFails()
    {
        using var cancel = new CancellationTokenSource();
        var flow = new PairingFlow(Nest.ParseAddress(NestAddress)!, "LAPTOP-3", handler: Trusting(), interval: TimeSpan.FromMilliseconds(100));
        flow.Changed += s =>
        {
            if (s.Stage == PairingStage.Waiting)
                cancel.Cancel();
        };
        var stopped = await flow.RunAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PairingStage.Waiting, stopped.Stage);

        var unreachable = await new PairingFlow(new Uri("http://127.0.0.1:9/"), "X").RunAsync(CancellationToken.None);
        Assert.Equal(PairingStage.Failed, unreachable.Stage);
        Assert.Contains("Can't reach", unreachable.Message);

        await using var noName = await TestServer.StartAsync();
        var oldServer = await new PairingFlow(noName.Url, "X").RunAsync(CancellationToken.None);
        Assert.Equal(PairingStage.Failed, oldServer.Stage); // no website to approve on
        Assert.Contains("no website", oldServer.Message);
    }
}
