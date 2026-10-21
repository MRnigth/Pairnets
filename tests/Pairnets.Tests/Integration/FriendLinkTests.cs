using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Friends (1.0.61.2) and linked nests (1.0.61.3) together, against the real server: a folder that came in through a link
/// is never shared on, not even with a friend who has the whole nest (listings, reading, writing, moving, "Changed");
/// the owner's linked folders and the nests shared with the owner's account are the owner's computers' only; and the
/// Files page's move, delete for good and "Where your files live" follow what a friend may do.
/// </summary>
public sealed class FriendLinkTests : IAsyncLifetime
{
    private const string LinkedIn = "From Ida – Work";

    private TestServer _server = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _owner = _server.RawHttp(_server.Token);
        foreach (var path in new[] { "Photos/beach.jpg", "Taxes/2026.pdf", LinkedIn + "/plan.txt" })
            Assert.Equal(HttpStatusCode.OK, (await PutAsync(_owner, path)).StatusCode);
        // Ida's nest shares "Work" with this nest; the key is made here (no key-shaped text in the repo).
        var key = AuthStore.KeyPrefix + Guid.NewGuid().ToString("N");
        Assert.NotNull(Links.Add("lnk_test0000000000000001", RelayFixtures.OtherNestId, "dev-ida", key, LinkedIn, LinkAccess.View, null, "Ida", "Work"));
    }

    public async Task DisposeAsync()
    {
        _owner.Dispose();
        await _server.DisposeAsync();
    }

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private LinkStore Links => _server.Services.GetRequiredService<LinkStore>();

    private HttpClient Friend(DeviceAccess access)
    {
        var (_, key) = Auth.AddDevice("Anna-Laptop", "Windows 11", "test", access, "Anna Berg", "acc_annaberg");
        return _server.RawHttp(key);
    }

    private static string Q(string path) => Uri.EscapeDataString(path);

    private static Task<HttpResponseMessage> PutAsync(HttpClient http, string path) =>
        http.PutAsync($"api/file?path={Q(path)}&base=none", new StringContent("from " + path));

    private static async Task AssertRefusedAsync(Task<HttpResponseMessage> call, string error, string what)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{what}: {(int)response.StatusCode} {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(error, doc.RootElement.GetProperty("error").GetString());
    }

    private static async Task<List<string>> ManifestPaths(HttpClient http) =>
        (await http.GetFromJsonAsync<List<ManifestEntry>>("api/manifest", PairnetsJson.Options))!.Where(e => !e.Deleted).Select(e => e.Path).ToList();

    [Fact]
    public async Task AFriendWithTheWholeNestNeverSeesAFolderThatCameInThroughALink()
    {
        using var anna = Friend(new DeviceAccess(DeviceAccess.Edit));
        Assert.DoesNotContain(LinkedIn + "/plan.txt", await ManifestPaths(anna));
        Assert.Contains("Photos/beach.jpg", await ManifestPaths(anna));
        Assert.Contains(LinkedIn + "/plan.txt", await ManifestPaths(_owner)); // the owner's computers see it as before

        await AssertRefusedAsync(anna.GetAsync($"api/file?path={Q(LinkedIn + "/plan.txt")}"), AccessGate.NotShared, "read");
        await AssertRefusedAsync(anna.GetAsync($"api/history?path={Q(LinkedIn + "/plan.txt")}"), AccessGate.NotShared, "history");
        await AssertRefusedAsync(PutAsync(anna, LinkedIn + "/new.txt"), AccessGate.NotShared, "write");
        await AssertRefusedAsync(anna.PostAsync($"api/move?from={Q("Photos/beach.jpg")}&to={Q(LinkedIn + "/beach.jpg")}&base=x", null),
            AccessGate.NotShared, "move in");
        await AssertRefusedAsync(anna.PostAsync($"api/move?from={Q(LinkedIn + "/plan.txt")}&to={Q("plan.txt")}&base=x", null),
            AccessGate.NotShared, "move out");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(anna, "Photos/anna.jpg")).StatusCode); // the rest of the nest as shared
    }

    [Fact]
    public void FriendsComputersNeverHearOfAChangeInALinkedFolder()
    {
        var friends = _server.Services.GetRequiredService<FriendConnections>();
        friends.Add("conn-anna", "dev-anna", new DeviceAccess(DeviceAccess.View));
        Assert.Contains("conn-anna", friends.Sharing("Photos/beach.jpg"));
        Assert.DoesNotContain("conn-anna", friends.Sharing(LinkedIn + "/plan.txt"));
        friends.Remove("conn-anna");
    }

    [Fact]
    public async Task TheOwnersLinkedFoldersAndSharedNestsAreTheOwnersComputersOnly()
    {
        Links.SetOffers([new SharedOffer(RelayFixtures.OtherNestId, "Cabin", "Ida", "view", null)]);
        using var anna = Friend(new DeviceAccess(DeviceAccess.Edit));
        await AssertRefusedAsync(anna.GetAsync("api/links"), AccessGate.OwnerOnly, "links");
        await AssertRefusedAsync(anna.GetAsync("api/offers"), AccessGate.OwnerOnly, "offers");

        var mine = _server.MintKey("STUDYPC");
        using var pc = _server.Client(mine.Id, mine.Key);
        Assert.Equal(LinkedIn, Assert.Single((await pc.GetLinkedFoldersAsync(CancellationToken.None))!).Folder);
        Assert.Equal("Cabin", Assert.Single((await pc.GetSharedNestOffersAsync(CancellationToken.None))!).NestName);

        // A friend's app asks for neither, and an answer of 403 counts as "none" anyway.
        var (device, key) = Auth.AddDevice("Anna-PC", "Windows 11", "test", new DeviceAccess(DeviceAccess.View), "Anna Berg", "acc_annaberg");
        using var annaApi = _server.Client(device.Id, key);
        Assert.Null(await annaApi.GetLinkedFoldersAsync(CancellationToken.None));
        Assert.Null(await annaApi.GetSharedNestOffersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MovingDeletingForGoodAndWhereFilesLiveFollowWhatAFriendMayDo()
    {
        using var viewer = Friend(new DeviceAccess(DeviceAccess.View));
        await AssertRefusedAsync(viewer.PostAsync($"api/move?from={Q("Photos/beach.jpg")}&to={Q("Photos/b.jpg")}&base=x", null), AccessGate.ViewOnly, "view move");

        using var photos = Friend(new DeviceAccess(DeviceAccess.Edit, ["Photos"]));
        await AssertRefusedAsync(photos.PostAsync($"api/move?from={Q("Photos/beach.jpg")}&to={Q("Taxes/beach.jpg")}&base=x", null), AccessGate.NotShared, "move out of the share");
        await AssertRefusedAsync(photos.PostAsync($"api/move?from={Q("Taxes/2026.pdf")}&to={Q("Photos/2026.pdf")}&base=x", null), AccessGate.NotShared, "move into the share");
        await AssertRefusedAsync(photos.PostAsync($"api/purge?path={Q("Photos/beach.jpg")}&base=x", null), AccessGate.OwnerOnly, "purge");
        await AssertRefusedAsync(photos.GetAsync("api/storage"), AccessGate.OwnerOnly, "storage");
        await AssertRefusedAsync(photos.PostAsync("api/storage/move", JsonContent.Create(new { to = "D:/x" })), AccessGate.OwnerOnly, "storage move");
        Assert.Contains("Taxes/2026.pdf", await ManifestPaths(_owner)); // nothing moved
    }
}
