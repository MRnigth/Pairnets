using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

public sealed partial class ApiTour
{
    /// <summary>
    /// Friend invites (1.0.61.2). On the tour's own nest every computer is the owner's: /api/access says so and
    /// /api/people lists nobody. A friend's computer only ever comes through the Pairnets service, so the rest runs on a
    /// second nest, linked, inside the test process whatever the target, like <see cref="LinkedNestAsync"/>.
    /// FriendAccessTests covers every case of the gate.
    /// </summary>
    private async Task FriendsAsync()
    {
        Step("On the tour's nest every computer is the owner's, and nobody else shares it");
        var mine = await Read<JsonElement>(Get(_laptopApi, "api/access"));
        Assert.Equal("owner", mine.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("folders").ValueKind);
        var nobody = await Read<JsonElement>(Get(_laptopApi, "api/people"));
        Assert.Empty(nobody.GetProperty("people").EnumerateArray());
        Assert.Empty(nobody.GetProperty("requests").EnumerateArray());
        await Refused(HttpMethod.Get, "api/access");
        await Refused(HttpMethod.Get, "api/people");
        foreach (var (method, url) in new[] { (HttpMethod.Patch, "api/relay/people/acc_x"), (HttpMethod.Delete, "api/relay/people/acc_x"), (HttpMethod.Put, "api/relay/requests") })
        {
            var refused = await Call(Api(_target.Token), method, url, new { }, HttpStatusCode.Unauthorized);
            Assert.Equal("bad_signature", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        Step("A linked nest lets a friend's computer in for one folder, view only: it sees that folder and changes nothing");
        await using var linked = await TestServer.StartAsync(config: RelayFixtures.Config(), configureBuilder: builder =>
            builder.Services.AddSingleton<IStartupFilter>(new AnsweredRoutesFilter(Record.ServerSaw)));
        using var service = new HttpClient(new RecordingHandler(Record, new SocketsHttpHandler { UseProxy = false })) { BaseAddress = linked.Url };
        async Task<JsonElement> Signed(HttpMethod method, string path, string? json = null)
        {
            using var response = await service.SendAsync(RelayFixtures.Signed(method, path, json));
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {path} answered {(int)response.StatusCode}. {Shorten(text)}");
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        HttpClient On(string key)
        {
            var http = new HttpClient(new RecordingHandler(Record, new SocketsHttpHandler { UseProxy = false })) { BaseAddress = linked.Url };
            http.DefaultRequestHeaders.Add(PairnetsHeaders.Token, key);
            _clients.Add(http);
            return http;
        }
        Task<HttpResponseMessage> PutFile(HttpClient http, string path, HttpStatusCode expect = HttpStatusCode.OK) =>
            Call(http, HttpMethod.Put, $"api/file?path={Uri.EscapeDataString(path)}&base=none", expect: expect,
                tweak: r => r.Content = new StringContent("from the tour"));
        async Task AssertRefusedWith(Task<HttpResponseMessage> call, string error) =>
            Assert.Equal(error, (await (await call).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var status = await Signed(HttpMethod.Get, "/api/relay/status");
        Assert.Contains("friends", status.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.False(status.GetProperty("sealed").GetBoolean());
        var desk = On((await Signed(HttpMethod.Post, "/api/relay/devices", """{"name":"DESK","system":"Windows 11"}""")).GetProperty("key").GetString()!);
        var added = await Signed(HttpMethod.Post, "/api/relay/devices",
            """{"name":"ANNA-PC","system":"Windows 11","access":"view","folders":["Photos"],"person":"Anna Berg · a•••@example.com","personId":"acc_annaberg"}""");
        var friendKey = added.GetProperty("key").GetString()!;
        var friend = On(friendKey);
        await PutFile(desk, "Photos/beach.jpg");
        await PutFile(desk, "Taxes/2026.pdf");
        await using var friendHub = await HubProbe.ConnectAsync("ANNA-PC's push channel", linked.Url, friendKey, "ANNA-PC", Record, HttpTransportType.WebSockets);
        // The client's connect call returns when the handshake is done; the server registers the connection (OnConnectedAsync)
        // just after that. A change made in between is not pushed to it (the apps sync again after connecting, so nothing is
        // lost), so wait until the server has the friend's connection before the tour changes anything. If it never does,
        // that is a real failure and says so.
        var friendConnections = linked.Services.GetRequiredService<FriendConnections>();
        var registeredBy = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (friendConnections.Sharing("Photos/beach.jpg").Count == 0)
        {
            if (DateTime.UtcNow > registeredBy)
                throw new TimeoutException("ANNA-PC's push channel connected, but the server never registered it as a friend's connection.");
            await Task.Delay(20);
        }

        var access = await Read<JsonElement>(Get(friend, "api/access"));
        Assert.Equal(("view", "Photos", "Anna Berg · a•••@example.com"),
            (access.GetProperty("access").GetString(), access.GetProperty("folders")[0].GetString(), access.GetProperty("person").GetString()));
        var manifest = await Read<List<ManifestEntry>>(Get(friend, "api/manifest"));
        Assert.Equal(new[] { "Photos/beach.jpg" }, manifest.Select(e => e.Path));
        await AssertRefusedWith(Get(friend, "api/file?path=Taxes%2F2026.pdf", HttpStatusCode.Forbidden), AccessGate.NotShared);
        await AssertRefusedWith(PutFile(friend, "Photos/new.jpg", HttpStatusCode.Forbidden), AccessGate.ViewOnly);
        await AssertRefusedWith(Get(friend, "api/people", HttpStatusCode.Forbidden), AccessGate.OwnerOnly);

        Step("The owner sees the friend and who waits under People; the service changes the share and then ends it");
        await Signed(HttpMethod.Put, "/api/relay/requests",
            """{"requests":[{"id":"inv_oskar","name":"Oskar Nilsson","email":"o•••@example.com","access":"edit","folders":null,"at":"2026-10-21T10:00:00Z"}]}""");
        var people = await Read<JsonElement>(Get(desk, "api/people"));
        var anna = Assert.Single(people.GetProperty("people").EnumerateArray().ToList());
        Assert.Equal(("acc_annaberg", "view"), (anna.GetProperty("personId").GetString(), anna.GetProperty("access").GetString()));
        Assert.Equal("ANNA-PC", Assert.Single(anna.GetProperty("devices").EnumerateArray().ToList()).GetProperty("name").GetString());
        Assert.Equal("Oskar Nilsson", Assert.Single(people.GetProperty("requests").EnumerateArray().ToList()).GetProperty("name").GetString());

        Assert.Equal(1, (await Signed(HttpMethod.Patch, "/api/relay/people/acc_annaberg", """{"access":"edit","folders":null}""")).GetProperty("devices").GetInt32());
        await PutFile(friend, "Taxes/notes.txt"); // the whole nest, and allowed to change it now
        await PutFile(desk, "Taxes/2027.pdf");
        await friendHub.WaitForAsync(SyncHub.ChangedMethod, args => (string?)args[1] == "Taxes/2027.pdf");

        Assert.Equal(1, (await Signed(HttpMethod.Delete, "/api/relay/people/acc_annaberg")).GetProperty("devices").GetInt32());
        await friendHub.WaitForAsync(SyncHub.DeviceRemovedMethod, args => (string?)args[0] == added.GetProperty("id").GetString());
        await friendHub.WaitClosedAsync("the friend's computer was removed");
        await Call(friend, HttpMethod.Get, "api/access", expect: HttpStatusCode.Unauthorized);
        Assert.Equal(0, (await Signed(HttpMethod.Delete, "/api/relay/people/acc_annaberg")).GetProperty("devices").GetInt32());
    }
}
