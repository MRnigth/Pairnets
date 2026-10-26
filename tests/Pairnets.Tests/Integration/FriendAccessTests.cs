using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Hashing;
using Pairnets.Server.Auth;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// The access gate against the real server (friend invites, 1.0.61.2): a friend's computer "View only" changes nothing,
/// one shared some folders reaches nothing outside them (also through an upload in pieces and history), what only the
/// owner does stays the owner's, listings and the push channel only carry what is shared, an encrypted nest is shared
/// whole only, and the owner's computers and the shared token see no difference at all.
/// </summary>
public sealed class FriendAccessTests : IAsyncLifetime
{
    private const string AnnaBerg = "Anna Berg · a•••@example.com";

    private TestServer _server = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _owner = _server.RawHttp(_server.Token); // the shared token is always the owner
        foreach (var path in new[] { "Photos/beach.jpg", "School/notes.txt", "Taxes/2026.pdf", "readme.txt" })
            await PutAsync(_owner, path, HttpStatusCode.OK);
    }

    public async Task DisposeAsync()
    {
        _owner.Dispose();
        await _server.DisposeAsync();
    }

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private static DeviceAccess ViewOnly(params string[] folders) => new(DeviceAccess.View, folders.Length == 0 ? null : folders);

    private static DeviceAccess CanChange(params string[] folders) => new(DeviceAccess.Edit, folders.Length == 0 ? null : folders);

    /// <summary>A friend's computer as the service adds it, and a client using its key.</summary>
    private (string Id, string Key, HttpClient Http) Friend(string name, DeviceAccess access, string personId = "acc_annaberg", string person = AnnaBerg)
    {
        var (device, key) = Auth.AddDevice(name, "Windows 11", "test", access, person, personId);
        _server.Services.GetRequiredService<DeviceRegistry>().Seen(new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey).RegistryKey, device.Name, null);
        return (device.Id, key, _server.RawHttp(key));
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient http, string path) =>
        http.PutAsync($"api/file?path={Uri.EscapeDataString(path)}&base=none", new StringContent("from " + path));

    private static async Task PutAsync(HttpClient http, string path, HttpStatusCode expect)
    {
        var response = await PutAsync(http, path);
        Assert.True(response.StatusCode == expect, $"PUT {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static string Q(string path) => Uri.EscapeDataString(path);

    /// <summary>Insists on a 403 with this error, under both names the apps and the service read.</summary>
    private static async Task AssertRefusedAsync(Task<HttpResponseMessage> call, string error, string what)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{what}: {(int)response.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(error, doc.RootElement.GetProperty("code").GetString());
    }

    private static async Task<List<string>> ManifestPaths(HttpClient http, string query = "") =>
        (await http.GetFromJsonAsync<List<ManifestEntry>>("api/manifest" + query, PairnetsJson.Options))!.Select(e => e.Path).Order(StringComparer.Ordinal).ToList();

    private async Task<string> StartUploadAsync(HttpClient http, string path, int size)
    {
        var response = await http.PostAsync($"api/upload?path={Q(path)}&base=none&size={size}&mtime=1700000000000", null);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"upload of {path}: {response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!.Id;
    }

    [Fact]
    public async Task ViewOnlyReadsEverythingSharedAndChangesNothing()
    {
        var (_, _, anna) = Friend("ANNA-PC", ViewOnly());
        using var annaHttp = anna;
        var before = _server.Store.CurrentVersion;

        Assert.Equal(4, (await ManifestPaths(anna)).Count);
        Assert.Equal("from Taxes/2026.pdf", await anna.GetStringAsync($"api/file?path={Q("Taxes/2026.pdf")}"));
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync($"api/history?path={Q("Taxes/2026.pdf")}")).StatusCode);

        await AssertRefusedAsync(PutAsync(anna, "Photos/new.jpg"), AccessGate.ViewOnly, "PUT");
        await AssertRefusedAsync(PutAsync(anna, "Taxes/2026.pdf"), AccessGate.ViewOnly, "PUT over a file");
        await AssertRefusedAsync(anna.DeleteAsync($"api/file?path={Q("readme.txt")}&base=none"), AccessGate.ViewOnly, "DELETE");
        await AssertRefusedAsync(anna.PostAsync($"api/upload?path={Q("Photos/big.bin")}&base=none&size=3", null), AccessGate.ViewOnly, "upload start");
        var ownersUpload = await StartUploadAsync(_owner, "Photos/big.bin", 3);
        await AssertRefusedAsync(anna.GetAsync($"api/upload/{ownersUpload}"), AccessGate.ViewOnly, "upload status");
        await AssertRefusedAsync(anna.PutAsync($"api/upload/{ownersUpload}?offset=0", new ByteArrayContent("abc"u8.ToArray())), AccessGate.ViewOnly, "upload piece");
        await AssertRefusedAsync(anna.PostAsync($"api/upload/{ownersUpload}/commit?hash={ContentHash.Of("abc"u8.ToArray())}", null), AccessGate.ViewOnly, "commit");
        await AssertRefusedAsync(anna.DeleteAsync($"api/upload/{ownersUpload}"), AccessGate.ViewOnly, "upload abort");
        await AssertRefusedAsync(anna.PostAsync($"api/history/restore?path={Q("readme.txt")}&id=x", null), AccessGate.ViewOnly, "restore");

        Assert.Equal(before, _server.Store.CurrentVersion);
        Assert.Equal(0, (await _owner.GetFromJsonAsync<UploadStatus>($"api/upload/{ownersUpload}", PairnetsJson.Options))!.Received);
    }

    [Theory]
    [InlineData("Photos/new.jpg", true)]
    [InlineData("Photos/2026/summer/new.jpg", true)]
    [InlineData("School/new.txt", true)]
    [InlineData("Taxes/new.pdf", false)]
    [InlineData("new.txt", false)]
    [InlineData("Photos2/new.jpg", false)]
    [InlineData("photos/new.jpg", false)]
    [InlineData("Photos/../Taxes/new.pdf", false)]
    [InlineData("Photos\\..\\Taxes\\new.pdf", false)]
    [InlineData("", false)]
    public async Task SharedFoldersLimitEveryPathAFriendNames(string path, bool shared)
    {
        var (_, _, anna) = Friend("ANNA-PC", CanChange("Photos", "School"));
        using var annaHttp = anna;

        var put = await PutAsync(anna, path);
        if (shared)
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync($"api/file?path={Q(path)}")).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await anna.PostAsync($"api/upload?path={Q(path + ".big")}&base=none&size=1", null)).StatusCode);
            return;
        }
        await AssertRefusedAsync(Task.FromResult(put), AccessGate.NotShared, "PUT " + path);
        await AssertRefusedAsync(anna.GetAsync($"api/file?path={Q(path)}"), AccessGate.NotShared, "GET " + path);
        await AssertRefusedAsync(anna.DeleteAsync($"api/file?path={Q(path)}&base=none"), AccessGate.NotShared, "DELETE " + path);
        await AssertRefusedAsync(anna.GetAsync($"api/history?path={Q(path)}"), AccessGate.NotShared, "history of " + path);
        await AssertRefusedAsync(anna.PostAsync($"api/history/restore?path={Q(path)}&id=x", null), AccessGate.NotShared, "restore of " + path);
        await AssertRefusedAsync(anna.PostAsync($"api/upload?path={Q(path)}&base=none&size=1", null), AccessGate.NotShared, "upload of " + path);
    }

    [Fact]
    public async Task AFileOutsideTheShareIsRefusedEvenWhenItExists()
    {
        var (_, _, anna) = Friend("ANNA-PC", CanChange("Photos"));
        using var annaHttp = anna;
        await AssertRefusedAsync(anna.GetAsync($"api/file?path={Q("Taxes/2026.pdf")}"), AccessGate.NotShared, "GET");
        var entry = (await _owner.GetFromJsonAsync<List<ManifestEntry>>("api/manifest", PairnetsJson.Options))!.Single(e => e.Path == "Taxes/2026.pdf");
        await AssertRefusedAsync(anna.DeleteAsync($"api/file?path={Q("Taxes/2026.pdf")}&base={entry.Hash}"), AccessGate.NotShared, "DELETE");
        Assert.Equal("from Taxes/2026.pdf", await _owner.GetStringAsync($"api/file?path={Q("Taxes/2026.pdf")}"));
    }

    [Fact]
    public async Task AnUploadInPiecesIsCheckedByThePathItWasStartedFor()
    {
        var (_, _, anna) = Friend("ANNA-PC", CanChange("Photos"));
        using var annaHttp = anna;
        var data = "abc"u8.ToArray();

        // The owner's upload of a file outside the share: none of its calls work for the friend, whatever id it knows.
        var owners = await StartUploadAsync(_owner, "Taxes/scan.pdf", data.Length);
        await AssertRefusedAsync(anna.PutAsync($"api/upload/{owners}?offset=0", new ByteArrayContent(data)), AccessGate.NotShared, "piece");
        await AssertRefusedAsync(anna.GetAsync($"api/upload/{owners}"), AccessGate.NotShared, "status");
        await AssertRefusedAsync(anna.PostAsync($"api/upload/{owners}/commit?hash={ContentHash.Of(data)}", null), AccessGate.NotShared, "commit");
        await AssertRefusedAsync(anna.DeleteAsync($"api/upload/{owners}"), AccessGate.NotShared, "abort");
        Assert.Equal(0, (await _owner.GetFromJsonAsync<UploadStatus>($"api/upload/{owners}", PairnetsJson.Options))!.Received);

        // Its own upload inside the share goes through, and an id the nest does not know is simply not found.
        var own = await StartUploadAsync(anna, "Photos/scan.jpg", data.Length);
        Assert.Equal(HttpStatusCode.OK, (await anna.PutAsync($"api/upload/{own}?offset=0", new ByteArrayContent(data))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anna.PostAsync($"api/upload/{own}/commit?hash={ContentHash.Of(data)}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync($"api/upload/{new string('0', 32)}")).StatusCode);
        Assert.Contains("Photos/scan.jpg", await ManifestPaths(_owner));
    }

    [Fact]
    public async Task TheManifestListsOnlyTheSharedFolders()
    {
        var (_, _, anna) = Friend("ANNA-PC", ViewOnly("Photos", "School"));
        var (_, _, jonas) = Friend("JONAS-PC", CanChange(), personId: "acc_jonas", person: "Jonas");
        using var a = anna;
        using var j = jonas;

        Assert.Equal(new[] { "Photos/beach.jpg", "School/notes.txt" }, await ManifestPaths(anna));
        Assert.Equal(4, (await ManifestPaths(jonas)).Count);
        Assert.Equal(4, (await ManifestPaths(_owner)).Count);

        var version = _server.Store.CurrentVersion;
        await PutAsync(_owner, "Taxes/2027.pdf", HttpStatusCode.OK);
        await PutAsync(_owner, "Photos/snow.jpg", HttpStatusCode.OK);
        var changes = await anna.GetAsync($"api/manifest?since={version}");
        Assert.Equal(new[] { "Photos/snow.jpg" }, (await changes.Content.ReadFromJsonAsync<List<ManifestEntry>>(PairnetsJson.Options))!.Select(e => e.Path));
        // The version still counts every change, so the next "since" starts after the ones it was not told about.
        Assert.Equal(_server.Store.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), changes.Headers.GetValues(PairnetsHeaders.Version).Single());
    }

    [Fact]
    public async Task OnlyTheOwnerManagesTheNest()
    {
        var desk = _server.MintKey("DESK");
        var (_, _, anna) = Friend("ANNA-PC", CanChange());
        using var annaHttp = anna;

        await AssertRefusedAsync(anna.DeleteAsync($"api/devices/{desk.Id}"), AccessGate.OwnerOnly, "removing another computer");
        await AssertRefusedAsync(anna.PatchAsJsonAsync($"api/devices/{desk.Id}/icon", new { icon = "laptop" }), AccessGate.OwnerOnly, "another computer's icon");
        await AssertRefusedAsync(anna.PostAsync("api/update", null), AccessGate.OwnerOnly, "updating the server");
        await AssertRefusedAsync(anna.GetAsync("api/update/diagnostics"), AccessGate.OwnerOnly, "the updater's diagnostics");
        await AssertRefusedAsync(anna.GetAsync("api/people"), AccessGate.OwnerOnly, "the People list");
        await AssertRefusedAsync(anna.PostAsync("api/keys/seal", null), AccessGate.OwnerOnly, "turning on encryption");
        await AssertRefusedAsync(anna.PutAsync("api/keys/recovery", new ByteArrayContent([1, 2, 3])), AccessGate.OwnerOnly, "the recovery envelope");
        await AssertRefusedAsync(anna.DeleteAsync("api/keys/envelopes/0123456789abcdef"), AccessGate.OwnerOnly, "a key envelope");
        await AssertRefusedAsync(anna.GetAsync("api/downloads"), AccessGate.OwnerOnly, "the nest's downloads");
        await AssertRefusedAsync(anna.PostAsJsonAsync("api/downloads", new { url = "https://example.com/a.iso" }), AccessGate.OwnerOnly, "a nest download");
        await AssertRefusedAsync(anna.DeleteAsync("api/downloads/0123456789abcdef"), AccessGate.OwnerOnly, "cancelling a nest download");
        await AssertRefusedAsync(anna.PostAsync("api/downloads/0123456789abcdef/retry", null), AccessGate.OwnerOnly, "trying a nest download again");

        Assert.True(Auth.GetDevice(desk.Id)!.IsActive);
        Assert.Null(Auth.GetDevice(desk.Id)!.Icon);

        // The owner's computers and the shared token are not held back.
        using var deskHttp = _server.RawHttp(desk.Key);
        Assert.Equal(HttpStatusCode.OK, (await deskHttp.PatchAsJsonAsync($"api/devices/{desk.Id}/icon", new { icon = "laptop" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deskHttp.GetAsync("api/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _owner.GetAsync("api/people")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deskHttp.GetAsync("api/update/diagnostics")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await deskHttp.GetAsync("api/downloads")).StatusCode);
    }

    [Fact]
    public async Task BeforeBigDeletesIsOneChoiceTheOwnerMakes()
    {
        var desk = _server.MintKey("DESK");
        using var deskHttp = _server.RawHttp(desk.Key);
        var (_, _, anna) = Friend("ANNA-PC", CanChange());
        using var annaHttp = anna;

        // Until the owner chooses, every computer asks (and every computer reads the choice).
        Assert.Equal(BigDeleteModes.AskEverywhere, (await anna.GetFromJsonAsync<ServerInfo>("api/info", PairnetsJson.Options))!.BigDeletes);

        await AssertRefusedAsync(anna.PutAsJsonAsync("api/settings/big-deletes", new BigDeletesRequest(BigDeleteModes.Never), PairnetsJson.Options),
            AccessGate.OwnerOnly, "the nest's \"Before big deletes\"");
        Assert.Equal(HttpStatusCode.BadRequest, (await deskHttp.PutAsJsonAsync("api/settings/big-deletes", new BigDeletesRequest("sometimes"), PairnetsJson.Options)).StatusCode);
        Assert.Equal(BigDeleteModes.AskEverywhere, Auth.BigDeletes);

        var set = await deskHttp.PutAsJsonAsync("api/settings/big-deletes", new BigDeletesRequest(BigDeleteModes.AskWhereDeleted), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(BigDeleteModes.AskWhereDeleted, Auth.BigDeletes);
        Assert.Equal(BigDeleteModes.AskWhereDeleted, (await anna.GetFromJsonAsync<ServerInfo>("api/info", PairnetsJson.Options))!.BigDeletes);
        Assert.Equal(BigDeleteModes.AskWhereDeleted, (await _owner.GetFromJsonAsync<ServerInfo>("api/info", PairnetsJson.Options))!.BigDeletes);
    }

    [Fact]
    public async Task AFriendRenamesAndLeavesWithItsOwnComputer()
    {
        var (id, _, anna) = Friend("ANNA-PC", ViewOnly("Photos"));
        using var annaHttp = anna;

        var renamed = await anna.PatchAsJsonAsync("api/devices/me", new { name = "Anna's laptop" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anna.PatchAsJsonAsync($"api/devices/{id}/icon", new { icon = "laptop" })).StatusCode);
        Assert.Equal(("Anna's laptop", "laptop"), (Auth.GetDevice(id)!.Name, Auth.GetDevice(id)!.Icon));

        Assert.Equal(HttpStatusCode.NoContent, (await anna.DeleteAsync($"api/devices/{id}")).StatusCode); // "Leave"
        Assert.False(Auth.GetDevice(id)!.IsActive);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anna.GetAsync("api/access")).StatusCode);
    }

    [Fact]
    public async Task AccessSaysWhatThisComputerMayDo()
    {
        Auth.NestName = "Home nest";
        var desk = _server.MintKey("DESK");
        using var deskHttp = _server.RawHttp(desk.Key);
        var (_, _, anna) = Friend("ANNA-PC", ViewOnly("Photos", "School"));
        using var annaHttp = anna;

        foreach (var owner in new[] { deskHttp, _owner })
        {
            var mine = await owner.GetFromJsonAsync<FriendEndpoints.AccessView>("api/access", PairnetsJson.Options);
            Assert.Equal(new FriendEndpoints.AccessView(DeviceAccess.Owner, null, null, "Home nest"), mine);
        }
        var json = await anna.GetStringAsync("api/access");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("view", doc.RootElement.GetProperty("access").GetString());
        Assert.Equal(new[] { "Photos", "School" }, doc.RootElement.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(AnnaBerg, doc.RootElement.GetProperty("person").GetString());
        Assert.Equal("Home nest", doc.RootElement.GetProperty("nestName").GetString());
        using var anonymous = _server.RawHttp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/access")).StatusCode);
    }

    [Fact]
    public async Task TheDevicesListSaysWhoseEachComputerIs()
    {
        var desk = _server.MintKey("DESK");
        using var deskHttp = _server.RawHttp(desk.Key);
        var (annaId, _, anna) = Friend("ANNA-PC", ViewOnly("Photos"));
        var (_, _, jonas) = Friend("JONAS-PC", CanChange(), personId: "acc_jonas", person: "Jonas");
        using var a = anna;
        using var j = jonas;
        using (var old = _server.RawHttp(_server.Token))
        {
            old.DefaultRequestHeaders.Add(PairnetsHeaders.DeviceId, "OLD-PC");
            await old.GetAsync("api/info"); // a computer still on the shared token
        }

        var all = (await deskHttp.GetFromJsonAsync<List<JsonElement>>("api/devices"))!;
        string? Field(List<JsonElement> list, string name, string field) =>
            list.Single(d => d.GetProperty("name").GetString() == name).GetProperty(field).GetString();
        Assert.Equal("owner", Field(all, "DESK", "access"));
        Assert.Null(Field(all, "DESK", "person"));
        Assert.Equal(("view", AnnaBerg), (Field(all, "ANNA-PC", "access"), Field(all, "ANNA-PC", "person")));
        Assert.Equal(("edit", "Jonas"), (Field(all, "JONAS-PC", "access"), Field(all, "JONAS-PC", "person")));
        Assert.Null(Field(all, "OLD-PC", "access"));
        // Still readable as the apps' DeviceInfo.
        Assert.Contains((await deskHttp.GetFromJsonAsync<List<DeviceInfo>>("api/devices", PairnetsJson.Options))!, d => d.Id == annaId);

        // A friend's computer sees the owner's computers and itself, never the other friends.
        var seen = (await anna.GetFromJsonAsync<List<JsonElement>>("api/devices"))!.Select(d => d.GetProperty("name").GetString()).ToList();
        Assert.Contains("DESK", seen);
        Assert.Contains("ANNA-PC", seen);
        Assert.Contains("OLD-PC", seen);
        Assert.DoesNotContain("JONAS-PC", seen);
    }

    [Fact]
    public async Task AnEncryptedNestIsOnlySharedWhole()
    {
        var (_, _, anna) = Friend("ANNA-PC", ViewOnly("Photos"));
        var (_, _, jonas) = Friend("JONAS-PC", ViewOnly(), personId: "acc_jonas", person: "Jonas");
        using var a = anna;
        using var j = jonas;
        _server.Store.Manifest.TurnOnSealed();
        foreach (var http in new[] { anna, jonas })
            http.DefaultRequestHeaders.Add(PairnetsHeaders.Features, PairnetsHeaders.SealedFeature);

        // With folders (which cannot happen through the service): refused everything but finding out and leaving.
        await AssertRefusedAsync(anna.GetAsync("api/manifest"), AccessGate.NotShared, "manifest");
        await AssertRefusedAsync(anna.GetAsync("api/info"), AccessGate.NotShared, "info");
        await AssertRefusedAsync(anna.GetAsync("api/keys"), AccessGate.NotShared, "keys");
        await AssertRefusedAsync(anna.PostAsync("hub/negotiate?negotiateVersion=1", null), AccessGate.NotShared, "push channel");
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("api/access")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("api/me")).StatusCode);

        // Shared whole: reads like the owner's computers do.
        Assert.Equal(HttpStatusCode.OK, (await jonas.GetAsync("api/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await jonas.GetAsync("api/keys")).StatusCode);
    }

    [Fact]
    public async Task ThePushChannelOnlyCarriesChangesInsideAFriendsFolders()
    {
        var (_, annaKey, anna) = Friend("ANNA-PC", ViewOnly("Photos"));
        var (_, jonasKey, jonas) = Friend("JONAS-PC", ViewOnly(), personId: "acc_jonas", person: "Jonas");
        var desk = _server.MintKey("DESK");
        using var a = anna;
        using var j = jonas;

        await using var annaHub = await ListenAsync(annaKey);
        await using var jonasHub = await ListenAsync(jonasKey);
        await using var deskHub = await ListenAsync(desk.Key);

        await PutAsync(_owner, "Taxes/2027.pdf", HttpStatusCode.OK);
        await PutAsync(_owner, "Photos/snow.jpg", HttpStatusCode.OK);

        // The push channel keeps the order of what one connection is sent: once the second change arrived, the first one
        // would have too.
        await annaHub.WaitForAsync("Photos/snow.jpg");
        Assert.DoesNotContain("Taxes/2027.pdf", annaHub.Paths);
        await jonasHub.WaitForAsync("Photos/snow.jpg");
        Assert.Contains("Taxes/2027.pdf", jonasHub.Paths);
        await deskHub.WaitForAsync("Photos/snow.jpg");
        Assert.Contains("Taxes/2027.pdf", deskHub.Paths);
    }

    private async Task<Listener> ListenAsync(string key)
    {
        var listener = new Listener(new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(key))
            .Build());
        await listener.Hub.StartAsync();
        await listener.Hub.InvokeAsync(nameof(SyncHub.Synced), 0L); // answered only once the nest has sorted the connection out
        return listener;
    }

    /// <summary>A push channel that notes every "Changed" path it hears.</summary>
    internal sealed class Listener : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _paths = new();
        private readonly SemaphoreSlim _heard = new(0);

        public Listener(HubConnection hub)
        {
            Hub = hub;
            hub.On<string, string>(SyncHub.ChangedMethod, (_, path) =>
            {
                _paths.Enqueue(path);
                _heard.Release();
            });
        }

        public HubConnection Hub { get; }

        public IReadOnlyCollection<string> Paths => _paths.ToArray();

        public async Task WaitForAsync(string path)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!_paths.Contains(path))
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await _heard.WaitAsync(left))
                    throw new TimeoutException($"Never heard Changed for {path}; heard: {string.Join(", ", _paths)}");
            }
        }

        public async ValueTask DisposeAsync() => await Hub.DisposeAsync();
    }

    [Fact]
    public async Task TheNestsOwnDevicesPageMarksAFriendsComputer()
    {
        await using var server = await TestServer.StartWithWebsiteAsync();
        var auth = server.Services.GetRequiredService<AuthStore>();
        var (device, _) = auth.AddDevice("ANNA-PC", "Windows 11", "test", ViewOnly("Photos"), AnnaBerg, "acc_annaberg");
        server.Services.GetRequiredService<DeviceRegistry>().Seen(new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey).RegistryKey, device.Name, null);
        var desk = server.MintKey("DESK");
        using var web = server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = auth.CreateSetupCode() })).StatusCode);

        var view = (await web.GetFromJsonAsync<WebEndpoints.DevicesView>("web/api/devices", PairnetsJson.Options))!;
        var anna = view.Devices.Single(d => d.Id == device.Id);
        Assert.Equal((DeviceAccess.View, AnnaBerg), (anna.Access, anna.Person));
        var own = view.Devices.Single(d => d.Id == desk.Id);
        Assert.Equal((DeviceAccess.Owner, (string?)null), (own.Access, own.Person));

        // The page writes "Anna Berg's · view only" (the name only, never the email part).
        var js = Encoding.UTF8.GetString(await web.GetByteArrayAsync("assets/nest.js"));
        Assert.Contains("function friendLine(d)", js);
        Assert.Contains("split(\" · \")[0]", js);
        Assert.Contains("\"view only\"", js);
        Assert.Contains("\"can change\"", js);
    }
}
