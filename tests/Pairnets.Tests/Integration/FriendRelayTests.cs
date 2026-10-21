using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;
using static Pairnets.Tests.Infrastructure.RelayFixtures;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Friend invites on a nest linked to Pairnets (cloud/RELAY.md §4), the service's signed side: the status says the nest
/// knows friends, a friend's computer is added with what it may do, a friend's share is changed or ended for all their
/// computers at once (live, also on the push channel), and the list of people waiting is kept for the owner's apps.
/// </summary>
public sealed class FriendRelayTests : IAsyncLifetime
{
    private const string AnnaBerg = "Anna Berg · a•••@example.com";

    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync(config: Config());

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? json = null)
    {
        using var http = _server.RawHttp();
        var response = await http.SendAsync(Signed(method, path, json));
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private async Task<JsonElement> OkAsync(HttpMethod method, string path, string? json = null)
    {
        var (status, body) = await SendAsync(method, path, json);
        Assert.True(status == HttpStatusCode.OK, $"{method} {path}: {status} {body}");
        return body;
    }

    private async Task AssertErrorAsync(HttpMethod method, string path, string json, string error)
    {
        var (status, body) = await SendAsync(method, path, json);
        Assert.True(status == HttpStatusCode.BadRequest, $"{json}: {status} {body}");
        Assert.Equal(error, body.GetProperty("error").GetString());
    }

    /// <summary>A friend's computer added the way the service adds it; its id and key.</summary>
    private async Task<(string Id, string Key)> AddFriendAsync(string name, string personId = "acc_annaberg", string access = "view",
        string folders = """["Photos"]""", string person = AnnaBerg)
    {
        var added = await OkAsync(HttpMethod.Post, "/api/relay/devices",
            $$"""{"name":"{{name}}","system":"Windows 11","access":"{{access}}","folders":{{folders}},"person":"{{person}}","personId":"{{personId}}"}""");
        return (added.GetProperty("id").GetString()!, added.GetProperty("key").GetString()!);
    }

    private async Task<(string Id, string Key)> AddOwnersAsync(string name, string? personId = null)
    {
        var body = personId is null ? $$"""{"name":"{{name}}","system":"macOS 15"}""" : $$"""{"name":"{{name}}","personId":"{{personId}}"}""";
        var added = await OkAsync(HttpMethod.Post, "/api/relay/devices", body);
        return (added.GetProperty("id").GetString()!, added.GetProperty("key").GetString()!);
    }

    private async Task PutAsync(string key, string path, HttpStatusCode expect = HttpStatusCode.OK)
    {
        using var http = _server.RawHttp(key);
        var response = await http.PutAsync($"api/file?path={Uri.EscapeDataString(path)}&base=none", new StringContent("from " + path));
        Assert.True(response.StatusCode == expect, $"PUT {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task TheStatusSaysTheNestKnowsFriendsAndWhetherItIsEncrypted()
    {
        var status = await OkAsync(HttpMethod.Get, "/api/relay/status");
        Assert.Equal(new[] { RelayEndpoints.FriendsFeature }, status.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.False(status.GetProperty("sealed").GetBoolean());

        _server.Store.Manifest.TurnOnSealed();
        Assert.True((await OkAsync(HttpMethod.Get, "/api/relay/status")).GetProperty("sealed").GetBoolean());
    }

    [Fact]
    public async Task AComputerWithoutAccessIsTheOwnersAsBefore()
    {
        var (id, _) = await AddOwnersAsync("DESK");

        var device = Auth.GetDevice(id)!;
        Assert.True(device.AccessOrOwner.IsOwner);
        Assert.Null(device.Person);
        var listed = (await OkAsync(HttpMethod.Get, "/api/relay/devices")).GetProperty("devices").EnumerateArray().Single();
        Assert.Equal("owner", listed.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("folders").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("person").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("personId").ValueKind);
    }

    [Fact]
    public async Task AFriendsComputerIsAddedWithWhatItMayDoAndWhoseItIs()
    {
        var (id, key) = await AddFriendAsync("ANNA-PC", folders: """["Photos","School"]""");

        var device = Auth.GetDevice(id)!;
        Assert.Equal((DeviceAccess.View, AnnaBerg, "acc_annaberg"), (device.AccessOrOwner.Level, device.Person, device.PersonId));
        Assert.Equal(new[] { "Photos", "School" }, device.AccessOrOwner.Folders!);
        var listed = (await OkAsync(HttpMethod.Get, "/api/relay/devices")).GetProperty("devices").EnumerateArray().Single();
        Assert.Equal(("view", AnnaBerg, "acc_annaberg"),
            (listed.GetProperty("access").GetString(), listed.GetProperty("person").GetString(), listed.GetProperty("personId").GetString()));
        Assert.Equal(new[] { "Photos", "School" }, listed.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));

        // Its key works, within what it may do.
        using var anna = _server.RawHttp(key);
        Assert.Equal("view", (await anna.GetFromJsonAsync<FriendEndpoints.AccessView>("api/access", PairnetsJson.Options))!.Access);
        await PutAsync(key, "Photos/new.jpg", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AFriendsComputerDoesNotTakeOverTheHistoryOfAnOwnersComputerWithTheSameName()
    {
        using (var old = _server.RawHttp(_server.Token))
        {
            old.DefaultRequestHeaders.Add(PairnetsHeaders.DeviceId, "LAPTOP");
            await old.GetAsync("api/info");
        }
        var (id, _) = await AddFriendAsync("LAPTOP");

        var devices = _server.Services.GetRequiredService<Pairnets.Server.Services.DeviceRegistry>().List();
        Assert.Contains(devices, d => d.Id is null && d.Name == "LAPTOP"); // the owner's old row stays the owner's
        Assert.DoesNotContain(devices, d => d.Id == id);
    }

    [Theory]
    [InlineData("""{"name":"X","access":"admin","personId":"acc_a"}""")]
    [InlineData("""{"name":"X","access":"view","folders":"Photos","personId":"acc_a"}""")]
    [InlineData("""{"name":"X","access":"view","folders":[],"personId":"acc_a"}""")]
    [InlineData("""{"name":"X","access":"view","folders":["Photos/2026"],"personId":"acc_a"}""")]
    [InlineData("""{"name":"X","access":"view","folders":[".."],"personId":"acc_a"}""")]
    [InlineData("""{"name":"X","access":"owner","folders":["Photos"]}""")]
    [InlineData("""{"name":"X","folders":["Photos"]}""")]
    [InlineData("""{"name":"X","access":"view"}""")] // a friend's computer needs whose it is
    [InlineData("""{"name":"X","access":"view","personId":"acc a"}""")]
    [InlineData("""{"name":"X","personId":"acc/a"}""")]
    public async Task BadAccessValuesAreRefusedAndNothingIsAdded(string json)
    {
        await AssertErrorAsync(HttpMethod.Post, "/api/relay/devices", json, "bad_access");
        Assert.Empty(Auth.ListDevices());
    }

    [Fact]
    public async Task AnEncryptedNestIsOnlySharedWhole()
    {
        _server.Store.Manifest.TurnOnSealed();

        await AssertErrorAsync(HttpMethod.Post, "/api/relay/devices",
            """{"name":"ANNA-PC","access":"view","folders":["Photos"],"personId":"acc_annaberg"}""", "sealed_whole_only");
        Assert.Empty(Auth.ListDevices());
        var (id, _) = await AddFriendAsync("ANNA-PC", folders: "null");
        Assert.Null(Auth.GetDevice(id)!.AccessOrOwner.Folders);

        await AssertErrorAsync(HttpMethod.Patch, "/api/relay/people/acc_annaberg", """{"access":"edit","folders":["Photos"]}""", "sealed_whole_only");
        Assert.Equal(1, (await OkAsync(HttpMethod.Patch, "/api/relay/people/acc_annaberg", """{"access":"edit","folders":null}""")).GetProperty("devices").GetInt32());
    }

    [Fact]
    public async Task ChangingAFriendsShareChangesAllTheirComputersAtOnceAndNoOneElses()
    {
        var (owner, ownerKey) = await AddOwnersAsync("DESK", personId: "acc_annaberg"); // the owner's, whatever it was sent with
        var (laptop, laptopKey) = await AddFriendAsync("ANNA-LAPTOP");
        var (pc, _) = await AddFriendAsync("ANNA-PC");
        var (jonas, _) = await AddFriendAsync("JONAS-PC", personId: "acc_jonas", person: "Jonas");
        await PutAsync(ownerKey, "Taxes/2026.pdf");

        // Used once, so the key is in the nest's cache with the old access.
        await PutAsync(laptopKey, "Taxes/new.pdf", HttpStatusCode.Forbidden);

        var changed = await OkAsync(HttpMethod.Patch, "/api/relay/people/acc_annaberg", """{"access":"edit","folders":["Taxes"]}""");
        Assert.Equal(2, changed.GetProperty("devices").GetInt32());

        foreach (var id in new[] { laptop, pc })
        {
            var access = Auth.GetDevice(id)!.AccessOrOwner;
            Assert.Equal((DeviceAccess.Edit, "Taxes"), (access.Level, string.Join(',', access.Folders!)));
        }
        Assert.True(Auth.GetDevice(owner)!.AccessOrOwner.IsOwner);
        Assert.Equal(DeviceAccess.View, Auth.GetDevice(jonas)!.AccessOrOwner.Level);
        await PutAsync(laptopKey, "Taxes/new.pdf"); // at once, not after the key cache runs out
        await PutAsync(laptopKey, "Photos/new.jpg", HttpStatusCode.Forbidden);

        // Asking again changes nothing; a person with no computers here is fine too.
        Assert.Equal(2, (await OkAsync(HttpMethod.Patch, "/api/relay/people/acc_annaberg", """{"access":"edit","folders":["Taxes"]}""")).GetProperty("devices").GetInt32());
        Assert.Equal(0, (await OkAsync(HttpMethod.Patch, "/api/relay/people/acc_nobody", """{"access":"view"}""")).GetProperty("devices").GetInt32());
    }

    [Theory]
    [InlineData("""{"access":"owner"}""", "bad_access")]
    [InlineData("""{}""", "bad_access")]
    [InlineData("""{"access":"view","folders":[]}""", "bad_access")]
    [InlineData("""{"access":"view","folders":["a/b"]}""", "bad_access")]
    [InlineData("""not json""", "bad_request")]
    public async Task ABadChangeIsRefusedAndChangesNothing(string json, string error)
    {
        var (id, _) = await AddFriendAsync("ANNA-PC");
        await AssertErrorAsync(HttpMethod.Patch, "/api/relay/people/acc_annaberg", json, error);
        Assert.Equal(DeviceAccess.View, Auth.GetDevice(id)!.AccessOrOwner.Level);
    }

    [Fact]
    public async Task AChangedShareReachesTheLivePushChannelAtOnce()
    {
        var (_, ownerKey) = await AddOwnersAsync("DESK");
        var (_, annaKey) = await AddFriendAsync("ANNA-PC");
        var heard = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(annaKey))
            .Build();
        var photo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taxes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<string, string>(SyncHub.ChangedMethod, (_, path) =>
        {
            heard.Enqueue(path);
            if (path == "Photos/a.jpg")
                photo.TrySetResult();
            if (path == "Taxes/b.pdf")
                taxes.TrySetResult();
        });
        await hub.StartAsync();
        await hub.InvokeAsync(nameof(SyncHub.Synced), 0L); // answered only once the nest has sorted the connection out

        await PutAsync(ownerKey, "Taxes/a.pdf");
        await PutAsync(ownerKey, "Photos/a.jpg");
        await photo.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.DoesNotContain("Taxes/a.pdf", heard);

        await OkAsync(HttpMethod.Patch, "/api/relay/people/acc_annaberg", """{"access":"view","folders":null}""");
        await PutAsync(ownerKey, "Taxes/b.pdf");
        await taxes.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task EndingAShareRemovesAllTheFriendsComputersAndNoOneElses()
    {
        var (owner, _) = await AddOwnersAsync("DESK", personId: "acc_annaberg");
        var (laptop, laptopKey) = await AddFriendAsync("ANNA-LAPTOP");
        var (pc, _) = await AddFriendAsync("ANNA-PC");
        var (jonas, _) = await AddFriendAsync("JONAS-PC", personId: "acc_jonas", person: "Jonas");
        using var anna = _server.RawHttp(laptopKey);
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("api/access")).StatusCode);

        var removedMessage = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(laptopKey))
            .Build();
        hub.On<string, string>(SyncHub.DeviceRemovedMethod, (removedId, _) =>
        {
            if (removedId == laptop)
                removedMessage.TrySetResult(removedId);
        });
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await hub.StartAsync();

        Assert.Equal(2, (await OkAsync(HttpMethod.Delete, "/api/relay/people/acc_annaberg")).GetProperty("devices").GetInt32());

        await removedMessage.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(Auth.GetDevice(laptop)!.IsActive);
        Assert.False(Auth.GetDevice(pc)!.IsActive);
        Assert.True(Auth.GetDevice(owner)!.IsActive);
        Assert.True(Auth.GetDevice(jonas)!.IsActive);
        var refused = await anna.GetAsync("api/access");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(ErrorCodes.DeviceRemoved, (await refused.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))!.Code);

        // Asking again is fine: there is nothing left to remove.
        Assert.Equal(0, (await OkAsync(HttpMethod.Delete, "/api/relay/people/acc_annaberg")).GetProperty("devices").GetInt32());
        Assert.Equal(0, (await OkAsync(HttpMethod.Delete, "/api/relay/people/not%20an%20id")).GetProperty("devices").GetInt32());
    }

    [Fact]
    public async Task ThePeopleWaitingAreKeptForTheOwnersAppsAndSurviveARestart()
    {
        var (_, ownerKey) = await AddOwnersAsync("DESK");
        var (annaId, annaKey) = await AddFriendAsync("ANNA-PC");
        await AddFriendAsync("ANNA-LAPTOP");
        var stored = await OkAsync(HttpMethod.Put, "/api/relay/requests", """
            {"requests":[
              {"id":"inv_oskar","name":"Oskar Nilsson","email":"o•••@example.com","access":"edit","folders":null,"at":"2026-10-21T10:00:00Z"},
              {"id":"inv_jonas","name":"Jonas","email":"j•••@example.com","access":"view","folders":["Photos"],"at":1792000000000}
            ]}
            """);
        Assert.Equal(2, stored.GetProperty("requests").GetInt32());

        async Task<JsonElement> PeopleAsync()
        {
            using var desk = _server.RawHttp(ownerKey);
            using var doc = JsonDocument.Parse(await desk.GetStringAsync("api/people"));
            return doc.RootElement.Clone();
        }

        var people = await PeopleAsync();
        var anna = Assert.Single(people.GetProperty("people").EnumerateArray().ToList());
        Assert.Equal(("acc_annaberg", AnnaBerg, "view"),
            (anna.GetProperty("personId").GetString(), anna.GetProperty("person").GetString(), anna.GetProperty("access").GetString()));
        Assert.Equal(new[] { "Photos" }, anna.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(new[] { "ANNA-LAPTOP", "ANNA-PC" },
            anna.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.Contains(anna.GetProperty("devices").EnumerateArray(), d => d.GetProperty("id").GetString() == annaId);
        var waiting = people.GetProperty("requests").EnumerateArray().ToList();
        Assert.Equal(new[] { "Oskar Nilsson", "Jonas" }, waiting.Select(r => r.GetProperty("name").GetString()));
        Assert.Equal("2026-10-21T10:00:00Z", waiting[0].GetProperty("at").GetString());
        Assert.Equal(1792000000000, waiting[1].GetProperty("at").GetInt64());
        Assert.Equal("Photos", waiting[1].GetProperty("folders")[0].GetString());

        // Only the owner's computers read them.
        using (var friend = _server.RawHttp(annaKey))
            Assert.Equal(HttpStatusCode.Forbidden, (await friend.GetAsync("api/people")).StatusCode);

        await _server.RestartAsync();
        Assert.Equal(2, (await PeopleAsync()).GetProperty("requests").GetArrayLength());

        // The service sends the whole list each time: an empty one clears it.
        await OkAsync(HttpMethod.Put, "/api/relay/requests", """{"requests":[]}""");
        Assert.Equal(0, (await PeopleAsync()).GetProperty("requests").GetArrayLength());
    }

    [Fact]
    public async Task ABadListOfPeopleWaitingIsRefusedAndTheOldOneKept()
    {
        await OkAsync(HttpMethod.Put, "/api/relay/requests", """{"requests":[{"id":"inv_oskar","name":"Oskar Nilsson","access":"view"}]}""");

        var tooMany = JsonSerializer.Serialize(new { requests = Enumerable.Range(1, FriendRequests.Max + 1).Select(i => new { id = $"inv_{i}", name = $"P{i}", access = "view" }) });
        await AssertErrorAsync(HttpMethod.Put, "/api/relay/requests", tooMany, "bad_request");
        await AssertErrorAsync(HttpMethod.Put, "/api/relay/requests", """{"requests":[{"id":"inv_1","access":"owner"}]}""", "bad_access");
        await AssertErrorAsync(HttpMethod.Put, "/api/relay/requests", """{"requests":[{"id":"inv_1","access":"view","folders":["a/b"]}]}""", "bad_access");
        await AssertErrorAsync(HttpMethod.Put, "/api/relay/requests", """{"requests":[{"name":"no id","access":"view"}]}""", "bad_request");
        await AssertErrorAsync(HttpMethod.Put, "/api/relay/requests", """{"people":[]}""", "bad_request");
        await AssertErrorAsync(HttpMethod.Put, "/api/relay/requests", """not json""", "bad_request");

        var kept = Assert.Single(_server.Services.GetRequiredService<FriendRequests>().List());
        Assert.Equal(("inv_oskar", "Oskar Nilsson"), (kept.Id, kept.Name));
    }

    [Fact]
    public async Task AFullListOfPeopleWaitingFitsInOneCall()
    {
        // Twenty people, each with twenty long folder names: longer than the 4 KiB the other calls may be.
        var folders = Enumerable.Range(1, DeviceAccess.MaxFolders).Select(i => $"{i:00} " + new string('f', 60)).ToList();
        var body = JsonSerializer.Serialize(new
        {
            requests = Enumerable.Range(1, FriendRequests.Max).Select(i => new { id = $"inv_{i}", name = $"Person {i}", access = "view", folders }),
        });
        Assert.True(body.Length > RelayAuthMiddleware.MaxBodyBytes && body.Length <= RelayAuthMiddleware.MaxListBodyBytes, body.Length.ToString());

        Assert.Equal(FriendRequests.Max, (await OkAsync(HttpMethod.Put, "/api/relay/requests", body)).GetProperty("requests").GetInt32());

        var tooLong = await SendAsync(HttpMethod.Put, "/api/relay/requests", """{"requests":[],"pad":""" + "\"" + new string('a', RelayAuthMiddleware.MaxListBodyBytes) + "\"}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLong.Status);
    }

    [Fact]
    public async Task TheNewCallsNeedTheServicesSignature()
    {
        var (_, ownerKey) = await AddOwnersAsync("DESK");
        await AddFriendAsync("ANNA-PC");
        using var desk = _server.RawHttp(ownerKey);
        foreach (var (method, path) in new[] { (HttpMethod.Patch, "api/relay/people/acc_annaberg"), (HttpMethod.Delete, "api/relay/people/acc_annaberg"), (HttpMethod.Put, "api/relay/requests") })
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { access = "edit", requests = Array.Empty<object>() }) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await desk.SendAsync(request)).StatusCode);
        }
        Assert.Single(Auth.ListFriendDevices("acc_annaberg"));
        Assert.Equal(DeviceAccess.View, Auth.ListFriendDevices("acc_annaberg")[0].AccessOrOwner.Level);
    }
}
