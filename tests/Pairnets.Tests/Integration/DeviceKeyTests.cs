using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Server.Auth;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Every computer with its own key: getting one, using it, renaming and removing it.</summary>
public sealed class DeviceKeyTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private DeviceKeys Keys => _server.Services.GetRequiredService<DeviceKeys>();

    private static async Task<string?> Code(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))?.Code;

    private DeviceKeyGrant Approve(string name) => _server.MintKey(name);

    [Fact]
    public async Task HelloNeedsNoSignInAndSaysWhatTheServerOffers()
    {
        await using var server = await TestServer.StartAsync(config: new() { ["Sync:PublicUrl"] = "https://nest.example.test" });
        using var anonymous = server.Client(token: "nothing");

        var hello = await anonymous.GetHelloAsync(CancellationToken.None);

        Assert.Equal("Pairnets", hello!.Product);
        Assert.Equal("https://nest.example.test", hello.PublicUrl);
        Assert.True(hello.DeviceKeys);
        // A fresh nest has no way to sign in set up yet; the apps show buttons from this.
        Assert.Equal(new SignInMethods(false, false, false, false), hello.Methods);
    }

    [Fact]
    public async Task AComputersOwnKeyWorksAndSaysWhoItIs()
    {
        var grant = Approve("LAPTOP");

        Assert.StartsWith("pn_", grant.Key);
        Assert.Equal("LAPTOP", grant.Name);
        using var own = _server.Client("whatever-it-claims", grant.Key);
        Assert.Equal(_server.Store.ServerId, (await own.GetInfoAsync(CancellationToken.None)).ServerId);
        var me = await own.GetMeAsync(CancellationToken.None);
        Assert.Equal((grant.Id, "LAPTOP", DeviceMe.KindDeviceKey), (me!.Id, me.Name, me.Kind));
    }

    [Fact]
    public async Task MeSaysWhoLetTheComputerIn()
    {
        var store = Keys.Store;
        store.SetOwnerEmail("owner@example.com");
        store.SetGoogleAccount("google-sub", "owner.google@example.com");
        var byGoogle = _server.MintKey("LAPTOP", "approved by the owner (google)");
        var byEmail = _server.MintKey("DESKTOP", "approved by the owner (email link)");
        var byOldNest = _server.MintKey("OLD", "approved by the owner");

        using (var api = _server.Client("x", byGoogle.Key))
        {
            var me = (await api.GetMeAsync(CancellationToken.None))!;
            Assert.Equal(("owner.google@example.com", "google"), (me.Email, me.Method));
            Assert.Equal(store.GetDevice(byGoogle.Id)!.Created, me.Added);
        }
        using (var api = _server.Client("x", byEmail.Key))
        {
            var me = (await api.GetMeAsync(CancellationToken.None))!;
            Assert.Equal(("owner@example.com", "email"), (me.Email, me.Method));
        }
        using (var api = _server.Client("x", byOldNest.Key))
        {
            var me = (await api.GetMeAsync(CancellationToken.None))!;
            Assert.Equal(("owner@example.com", (string?)null), (me.Email, me.Method)); // the sign-in was not written down
            Assert.NotNull(me.Added);
        }
        store.SetOwnerEmail(null);
        using (var api = _server.Client("x", byEmail.Key))
            Assert.Equal("owner.google@example.com", (await api.GetMeAsync(CancellationToken.None))!.Email);

        // The shared token is no one in particular.
        using var shared = _server.Client("MAC");
        var anyone = (await shared.GetMeAsync(CancellationToken.None))!;
        Assert.Equal((DeviceMe.KindSharedToken, (string?)null, (string?)null, (DateTimeOffset?)null), (anyone.Kind, anyone.Email, anyone.Method, anyone.Added));
    }

    [Fact]
    public async Task ASessionWithItsOwnKeyShowsWhoLetItIn()
    {
        Keys.Store.SetOwnerEmail("owner@example.com");
        var grant = _server.MintKey("MAC", "approved by the owner (passkey)");
        using var folder = new TempDir("account");
        using var stateBase = new TempDir("account-state");
        await using var session = ClientSession.Start(new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused",
            Folder = folder.Path,
            DeviceName = "MAC",
            DeviceId = grant.Id,
            FirstRunCompleted = true,
        }, grant.Key, new PermanentDeleteTrash(), stateBaseDir: stateBase.Path,
            runnerOptions: o => new RunnerOptions { ServerUrl = o.ServerUrl, Token = o.Token, DeviceId = o.DeviceId, PeriodicInterval = TimeSpan.FromHours(1) });

        // The session checks its account on start-up.
        await WaitUntil(() => session.Status.Account is not null, "account");
        var account = session.Status.Account!;
        Assert.Equal(("owner@example.com", "passkey", "Signed in with a passkey"), (account.Email, account.Method, account.MethodText));
        Assert.Equal(Keys.Store.GetDevice(grant.Id)!.Created, account.Added);
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "first pass");
        Assert.Equal(account, session.Status.Account); // kept as the status changes
    }

    [Fact]
    public async Task NoEndpointHandsOutKeysWithoutAPerson()
    {
        // The silent POST /api/devices/upgrade is gone: Allow on the nest is the only way to a key.
        // (The path now only matches DELETE /api/devices/{id}, so a POST gets "method not allowed".)
        using var shared = _server.RawHttp(_server.Token);
        var gone = await shared.PostAsJsonAsync("api/devices/upgrade", new DeviceNameRequest("LAPTOP"), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, gone.StatusCode);
    }

    [Fact]
    public async Task AComputerWithItsOwnKeyCannotPretendToBeAnother()
    {
        var grant = Approve("LAPTOP");
        using var own = _server.Client("DESKTOP", grant.Key);
        using var sync = new MemoryStream("x"u8.ToArray());

        await own.UploadAsync("a.txt", "none", 1_700_000_000_000, sync, null, CancellationToken.None);

        var devices = await own.GetDevicesAsync(CancellationToken.None);
        Assert.Contains(devices!, d => d.Name == "LAPTOP" && d.Id == grant.Id && d.LastChange is not null);
        Assert.DoesNotContain(devices!, d => d.Name == "DESKTOP");
        Assert.Equal(DeviceMe.KindDeviceKey, (await own.GetMeAsync(CancellationToken.None))!.Kind);
    }

    [Fact]
    public async Task ARemovedComputerIsToldSoAndDropsOutOfTheList()
    {
        var laptop = Approve("LAPTOP");
        var desktop = Approve("DESKTOP");
        using var laptopApi = _server.Client("LAPTOP", laptop.Key);
        using var desktopApi = _server.Client("DESKTOP", desktop.Key);
        await laptopApi.GetInfoAsync(CancellationToken.None);

        Assert.True(await desktopApi.RemoveDeviceAsync(laptop.Id, CancellationToken.None));
        Assert.False(await desktopApi.RemoveDeviceAsync(laptop.Id, CancellationToken.None));

        var refused = await Assert.ThrowsAsync<PairnetsAuthException>(() => laptopApi.GetInfoAsync(CancellationToken.None));
        Assert.Equal(ErrorCodes.DeviceRemoved, refused.Code);
        Assert.True(refused.NeedsSignIn);
        var devices = await desktopApi.GetDevicesAsync(CancellationToken.None);
        Assert.DoesNotContain(devices!, d => d.Id == laptop.Id);
        Assert.Equal(HttpStatusCode.OK, (await desktopApi.HealthAsync(CancellationToken.None)) ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARemovedComputerHearsAboutItAndItsPushChannelIsClosed()
    {
        var laptop = Approve("LAPTOP");
        var desktop = Approve("DESKTOP");
        var removedMessage = new TaskCompletionSource<(string Id, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(laptop.Key))
            .Build();
        hub.On<string, string>(SyncHub.DeviceRemovedMethod, (id, name) => removedMessage.TrySetResult((id, name)));
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.StartAsync();

        using var desktopApi = _server.Client("DESKTOP", desktop.Key);
        await desktopApi.RemoveDeviceAsync(laptop.Id, CancellationToken.None);

        Assert.Equal((laptop.Id, "LAPTOP"), await removedMessage.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reconnect = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, reconnect.StatusCode);
    }

    [Fact]
    public async Task TurningOffTheSharedTokenLeavesOnlyOwnKeys()
    {
        var grant = Approve("LAPTOP");
        Keys.AllowSharedToken = false;

        using var shared = _server.Client("DESKTOP");
        var refused = await Assert.ThrowsAsync<PairnetsAuthException>(() => shared.GetInfoAsync(CancellationToken.None));
        Assert.Equal(ErrorCodes.SharedTokenOff, refused.Code);
        using var own = _server.Client("LAPTOP", grant.Key);
        await own.GetInfoAsync(CancellationToken.None);
        // A wrong token is still just a wrong token.
        using var wrong = _server.RawHttp("wrong-token-wrong-token");
        Assert.Equal(ErrorCodes.Unauthorized, await Code(await wrong.GetAsync("api/info")));
    }

    [Fact]
    public async Task RenamingKeepsNamesUniqueAndTellsTheOthers()
    {
        var laptop = Approve("LAPTOP");
        var desktop = Approve("DESKTOP");
        using var desktopApi = _server.Client("DESKTOP", desktop.Key);

        var renamed = await desktopApi.RenameThisDeviceAsync("laptop", CancellationToken.None);

        Assert.Equal("laptop (2)", renamed.Name);
        Assert.Equal("laptop (2)", (await desktopApi.GetMeAsync(CancellationToken.None))!.Name);
        using var shared = _server.Client("OLD");
        await Assert.ThrowsAsync<PairnetsProtocolException>(() => shared.RenameThisDeviceAsync("x", CancellationToken.None));
        Assert.Equal(laptop.Id, (await desktopApi.GetDevicesAsync(CancellationToken.None))!.Single(d => d.Name == "LAPTOP").Id);
    }

    [Fact]
    public async Task KeysAreNeverLogged()
    {
        var grant = Approve("LAPTOP");
        using var own = _server.Client("LAPTOP", grant.Key);
        await own.GetInfoAsync(CancellationToken.None);
        using var anon = _server.RawHttp();
        await anon.PostAsync($"hub/negotiate?negotiateVersion=1&access_token={grant.Key}", null);
        await own.RemoveDeviceAsync(grant.Id, CancellationToken.None).ContinueWith(_ => { });
        await own.GetInfoAsync(CancellationToken.None).ContinueWith(_ => { });

        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(grant.Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryEndpointNeedsAKeyExceptTheFewPublicOnes()
    {
        var routes = _server.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Pattern: "/" + e.RoutePattern.RawText!.TrimStart('/'), Methods: e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"]))
            .ToList();
        Assert.DoesNotContain(routes, r => r.Pattern == "/api/devices/upgrade"); // keys only come from Allow on the nest
        using var anonymous = _server.RawHttp();
        var throttle = _server.Services.GetRequiredService<FailureThrottle>();

        foreach (var (pattern, methods) in routes)
        {
            var url = pattern.Replace("{id}", "x", StringComparison.Ordinal).TrimStart('/');
            foreach (var method in methods)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                var status = (await anonymous.SendAsync(request)).StatusCode;
                throttle.RecordSuccess(IPAddress.Loopback); // so the brute-force delay does not slow this test down
                if (TokenAuthMiddleware.IsPublic(new Microsoft.AspNetCore.Http.PathString(pattern)))
                    Assert.NotEqual(HttpStatusCode.Unauthorized, status);
                else
                    Assert.True(status == HttpStatusCode.Unauthorized, $"{method} {pattern} answered {status} without a key");
            }
        }
    }

    [Fact]
    public async Task ASharedTokenSessionStopsUntilAPersonSignsItIn()
    {
        // The nest has its own name, so it can sign computers in: no key arrives silently any more.
        await using var server = await TestServer.StartAsync(config: new() { ["Sync:PublicUrl"] = "https://nest.example.test" });
        using var folder = new TempDir("needs-signin");
        using var stateBase = new TempDir("needs-signin-state");
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "kept.txt"), "untouched");
        var settings = new ClientSettings
        {
            ServerUrl = server.Url.ToString(),
            ProtectedToken = "unused",
            Folder = folder.Path,
            DeviceName = "MAC",
            FirstRunCompleted = true,
        };
        await using var session = ClientSession.Start(settings, server.Token, new PermanentDeleteTrash(), stateBaseDir: stateBase.Path,
            runnerOptions: o => new RunnerOptions { ServerUrl = o.ServerUrl, Token = o.Token, DeviceId = o.DeviceId, PeriodicInterval = TimeSpan.FromHours(1) });
        var changed = false;
        session.AccountChanged += (_, _) => changed = true;

        await session.CheckAccountAsync(await session.Api.GetInfoAsync(CancellationToken.None));

        await WaitUntil(() => session.Status is { BlockReason: BlockReason.SignInRequired, Status: RunnerStatus.Blocked }, "sign-in stop",
            () => $"{session.Status.Status} {session.Status.BlockReason} {session.Status.Text}");
        var status = session.Status;
        Assert.Equal("Sign in to your nest", status.Headline);
        Assert.Equal("Sign in with your browser…", status.FixLabel);
        Assert.False(changed); // no key was fetched behind the owner's back
        Assert.False(settings.HasOwnKey); // settings and folder stay exactly as they were
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(folder.Path, "kept.txt")));
    }

    [Fact]
    public async Task ASharedTokenSessionAgainstAServerWithoutSignInKeepsSyncing()
    {
        // This nest has no name of its own yet, so there is nothing to sign in to: the shared token still works.
        using var folder = new TempDir("old-server");
        using var stateBase = new TempDir("old-server-state");
        var settings = new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused",
            Folder = folder.Path,
            DeviceName = "MAC",
            FirstRunCompleted = true,
        };
        await using var session = ClientSession.Start(settings, _server.Token, new PermanentDeleteTrash(), stateBaseDir: stateBase.Path,
            runnerOptions: o => new RunnerOptions { ServerUrl = o.ServerUrl, Token = o.Token, DeviceId = o.DeviceId, PeriodicInterval = TimeSpan.FromHours(1) });

        await session.CheckAccountAsync(await session.Api.GetInfoAsync(CancellationToken.None));

        Assert.False(session.Engine.SignInRequired);
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "first pass");
    }

    [Fact]
    public async Task ARemovedComputerStopsWithSignInAgain()
    {
        var grant = Approve("MAC");
        using var folder = new TempDir("removed");
        using var stateBase = new TempDir("removed-state");
        await using var session = ClientSession.Start(new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused",
            Folder = folder.Path,
            DeviceName = "MAC",
            DeviceId = grant.Id,
            FirstRunCompleted = true,
        }, grant.Key, new PermanentDeleteTrash(), stateBaseDir: stateBase.Path,
            runnerOptions: o => new RunnerOptions { ServerUrl = o.ServerUrl, Token = o.Token, DeviceId = o.DeviceId, PeriodicInterval = TimeSpan.FromHours(1) });
        await WaitUntil(() => session.Status.Status == RunnerStatus.Idle, "first pass");

        Assert.True(await session.SignOutAsync());

        // Several triggers (the push message, the closed push channel) may each run a pass; wait for one to settle.
        await WaitUntil(() => session.Status is { BlockReason: BlockReason.SignedOut, Status: RunnerStatus.Blocked }, "signed-out stop",
            () => $"{session.Status.Status} {session.Status.BlockReason} {session.Status.Text}");
        var status = session.Status;
        Assert.Equal("Signed out of your nest", status.Headline);
        Assert.Equal("Sign in again…", status.FixLabel);
        Assert.Contains("removed", status.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavingSettingsKeepsTheKeyAndSendsANewNameToTheNest()
    {
        var laptop = Approve("LAPTOP");
        Approve("DESK");
        var original = new ClientSettings { ServerUrl = _server.Url.ToString(), DeviceName = "LAPTOP", DeviceId = laptop.Id };

        Assert.Equal((laptop.Id, "LAPTOP"), await OwnKey.CarryOverAsync(original, _server.Url, laptop.Key, tokenTyped: false, "LAPTOP"));
        Assert.Equal((laptop.Id, "DESK (2)"), await OwnKey.CarryOverAsync(original, _server.Url, laptop.Key, tokenTyped: false, "DESK"));
        Assert.Equal(((string?)null, "OTHER"), await OwnKey.CarryOverAsync(original, _server.Url, _server.Token, tokenTyped: true, "OTHER"));
        Assert.Equal((laptop.Id, "LAPTOP"), await OwnKey.CarryOverAsync(original, new Uri("http://127.0.0.1:9/"), laptop.Key, tokenTyped: false, "NEW"));
    }

    private static async Task WaitUntil(Func<bool> condition, string what, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(what + (state is null ? string.Empty : ": " + state()));
            await Task.Delay(50);
        }
    }
}
