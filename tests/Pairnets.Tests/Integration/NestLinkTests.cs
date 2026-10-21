using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;
using static Pairnets.Tests.Infrastructure.RelayFixtures;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Linked nests (docs/DECISIONS.md, "4 nests per account, and linked nests"), with two real nests. Oskar's nest
/// shares, and Anna's nest joins: the service sets the link up on Anna's nest (signed /api/relay/links), and Anna's
/// nest then keeps "From Oskar – Home nest" in step with Oskar's nest, using a key of Oskar's nest like a computer
/// would. Covered: files in, deletes, view only, edit both ways, a change on both sides, only the shared folders,
/// unlink keeping the copy, the limits and refusals, and what the apps read.
/// </summary>
public sealed class NestLinkTests : IAsyncLifetime
{
    private const string LinkId = "lnk_test0000000000000001";
    private const string Folder = "From Oskar – Home nest";

    private TestServer _oskar = null!;
    private TestServer _anna = null!;
    private DeviceKeyGrant _linkKey = null!;
    private PairnetsApiClient _oskarPc = null!;

    public async Task InitializeAsync()
    {
        _oskar = await TestServer.StartAsync();
        var config = Config();
        config["Sync:LinkInitialDelay"] = "01:00:00"; // the tests run the passes themselves
        config["Sync:LinkInterval"] = "01:00:00";
        _anna = await TestServer.StartAsync(config: config);
        _linkKey = _oskar.MintKey("Anna's Home nest");
        var pc = _oskar.MintKey("STUDYPC");
        _oskarPc = _oskar.Client(pc.Id, pc.Key);
        Worker.Connect = _ => _oskar.Client(_linkKey.Id, _linkKey.Key);
    }

    public async Task DisposeAsync()
    {
        _oskarPc.Dispose();
        await _anna.DisposeAsync();
        await _oskar.DisposeAsync();
    }

    private NestLinkWorker Worker => _anna.Services.GetRequiredService<NestLinkWorker>();

    private LinkStore Links => _anna.Services.GetRequiredService<LinkStore>();

    private async Task<HttpResponseMessage> RelayAsync(HttpMethod method, string path, object? body = null)
    {
        using var http = _anna.RawHttp();
        return await http.SendAsync(Signed(method, path, body is null ? null : JsonSerializer.Serialize(body, PairnetsJson.Options)));
    }

    private Task<HttpResponseMessage> LinkAsync(string access = "view", string[]? folders = null, string id = LinkId) =>
        RelayAsync(HttpMethod.Post, "/api/relay/links", new
        {
            id,
            remoteNestId = OtherNestId,
            deviceId = _linkKey.Id,
            key = _linkKey.Key,
            access,
            folders,
            ownerName = "Oskar",
            nestName = "Home nest",
        });

    private async Task PutAsync(PairnetsApiClient api, string path, string text, string baseHash = "none")
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var (result, _, _) = await api.UploadAsync(path, baseHash, 1_700_000_000_000, body, null, CancellationToken.None);
        Assert.Equal(ApiOutcome.Ok, result.Outcome);
    }

    private static async Task<string?> ReadAsync(SyncStore store, string path)
    {
        var opened = await store.OpenReadAsync(path, CancellationToken.None);
        if (opened is null)
            return null;
        await using (opened.Stream)
        using (var reader = new StreamReader(opened.Stream))
            return await reader.ReadToEndAsync();
    }

    private static async Task PutLocalAsync(SyncStore store, string path, string text)
    {
        var current = store.Manifest.Get(path);
        var result = await store.PutAsync(path, current is { Deleted: false } ? current.Hash! : "none", 1_700_000_000_000,
            new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);
        Assert.Equal(ChangeStatus.Ok, result.Status);
    }

    private async Task<string> HashOnOskarAsync(string path) =>
        (await _oskarPc.GetManifestAsync(null, CancellationToken.None)).Entries.Single(e => e.Path == path && !e.Deleted).Hash!;

    private Task PassAsync() => Worker.RunAllAsync(CancellationToken.None);

    [Fact]
    public async Task FilesComeInToTheLinkedFolderAndDeletesFollow()
    {
        await PutAsync(_oskarPc, "Photos/beach.jpg", "sand");
        await PutAsync(_oskarPc, "Notes.txt", "hello");
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Assert.Equal(Folder, (await made.Content.ReadFromJsonAsync<LinkEndpoints.LinkView>(PairnetsJson.Options))!.Folder);

        await PassAsync();
        Assert.Equal("sand", await ReadAsync(_anna.Store, Folder + "/Photos/beach.jpg"));
        Assert.Equal("hello", await ReadAsync(_anna.Store, Folder + "/Notes.txt"));
        Assert.Equal(LinkState.InStep, Links.Get(LinkId)!.State);

        var gone = await _oskarPc.DeleteAsync("Notes.txt", await HashOnOskarAsync("Notes.txt"), CancellationToken.None);
        Assert.Equal(ApiOutcome.Ok, gone.Outcome);
        await PutAsync(_oskarPc, "Photos/beach.jpg", "waves", await HashOnOskarAsync("Photos/beach.jpg"));
        await PassAsync();
        Assert.Null(await ReadAsync(_anna.Store, Folder + "/Notes.txt"));
        Assert.Equal("waves", await ReadAsync(_anna.Store, Folder + "/Photos/beach.jpg"));
        Assert.NotEmpty(_anna.Store.ListHistory(Folder + "/Photos/beach.jpg")); // the old version went to history as usual
    }

    [Fact]
    public async Task ViewOnlyKeepsLocalChangesHereAndSendsNothing()
    {
        await PutAsync(_oskarPc, "Photos/beach.jpg", "sand");
        await LinkAsync("view");
        await PassAsync();

        await PutLocalAsync(_anna.Store, Folder + "/Photos/mine.jpg", "anna's");
        await PassAsync();
        Assert.DoesNotContain((await _oskarPc.GetManifestAsync(null, CancellationToken.None)).Entries, e => e.Path == "Photos/mine.jpg");
        Assert.Equal("anna's", await ReadAsync(_anna.Store, Folder + "/Photos/mine.jpg"));
    }

    [Fact]
    public async Task EditSendsChangesBothWaysAndAChangeOnBothSidesKeepsBoth()
    {
        await PutAsync(_oskarPc, "School/plan.txt", "v1");
        await LinkAsync("edit");
        await PassAsync();

        await PutLocalAsync(_anna.Store, Folder + "/School/new.txt", "from anna");
        await PassAsync();
        Assert.Contains((await _oskarPc.GetManifestAsync(null, CancellationToken.None)).Entries, e => e.Path == "School/new.txt" && !e.Deleted);

        // Both change plan.txt before the next pass: Oskar's version keeps the name, Anna's nest keeps its own as a copy.
        await PutAsync(_oskarPc, "School/plan.txt", "oskar v2", await HashOnOskarAsync("School/plan.txt"));
        await PutLocalAsync(_anna.Store, Folder + "/School/plan.txt", "anna v2");
        await PassAsync();
        Assert.Equal("oskar v2", await ReadAsync(_anna.Store, Folder + "/School/plan.txt"));
        var copies = _anna.Store.ReadManifest(null).Entries.Where(e => !e.Deleted && e.Path.Contains("(conflict this nest", StringComparison.Ordinal)).ToList();
        Assert.Single(copies);
        Assert.Equal("anna v2", await ReadAsync(_anna.Store, copies[0].Path));
    }

    [Fact]
    public async Task OnlyTheSharedFoldersComeIn()
    {
        await PutAsync(_oskarPc, "Photos/beach.jpg", "sand");
        await PutAsync(_oskarPc, "Work/secret.txt", "no");
        await LinkAsync(folders: ["Photos"]);
        await PassAsync();
        Assert.Equal("sand", await ReadAsync(_anna.Store, Folder + "/Photos/beach.jpg"));
        Assert.Null(await ReadAsync(_anna.Store, Folder + "/Work/secret.txt"));
    }

    [Fact]
    public async Task UnlinkForgetsTheKeyAndKeepsTheCopy()
    {
        await PutAsync(_oskarPc, "Photos/beach.jpg", "sand");
        await LinkAsync();
        await PassAsync();

        Assert.Equal(HttpStatusCode.OK, (await RelayAsync(HttpMethod.Delete, "/api/relay/links/" + LinkId)).StatusCode);
        await PutAsync(_oskarPc, "Photos/beach.jpg", "waves", await HashOnOskarAsync("Photos/beach.jpg"));
        await PassAsync();
        Assert.Equal("sand", await ReadAsync(_anna.Store, Folder + "/Photos/beach.jpg"));
        var link = Links.Get(LinkId)!;
        Assert.Null(link.Key);
        Assert.Equal(LinkState.Unlinked, link.State);
    }

    [Fact]
    public async Task AKeyRemovedOnTheOtherNestShowsAProblem()
    {
        await LinkAsync();
        Worker.Connect = _ => _oskar.Client(_linkKey.Id, "pn_test");
        await PassAsync();
        Assert.Equal(LinkState.Problem, Links.Get(LinkId)!.State);
    }

    [Fact]
    public async Task TheAppsSeeTheLinkedFolderAndItsState()
    {
        await LinkAsync();
        await PassAsync();
        var me = _anna.MintKey("ANNA-PC");
        using var http = _anna.RawHttp();
        http.DefaultRequestHeaders.Authorization = new("Bearer", me.Key);
        var list = await http.GetFromJsonAsync<LinkEndpoints.AppLinkList>("api/links", PairnetsJson.Options);
        var link = Assert.Single(list!.Links);
        Assert.Equal(Folder, link.Folder);
        Assert.Equal("Oskar", link.OwnerName);
        Assert.Equal("view", link.Access);
        Assert.Equal("in_step", link.State);
        Assert.DoesNotContain(_linkKey.Key, await http.GetStringAsync("api/links"));
    }

    [Fact]
    public async Task LimitsAndRefusals()
    {
        // Unsigned calls are refused like the rest of /api/relay.
        using (var http = _anna.RawHttp())
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("api/relay/links", new StringContent("{}"))).StatusCode);

        for (var i = 1; i <= LinkStore.MaxLinks; i++)
            Assert.Equal(HttpStatusCode.OK, (await LinkAsync(id: $"lnk_test000000000000000{i}x")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await LinkAsync(id: "lnk_test0000000000000099")).StatusCode);

        // The same link sent again keeps its folder; each other link got a folder of its own.
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(id: "lnk_test0000000000000001x")).StatusCode);
        Assert.Equal(LinkStore.MaxLinks, Links.List().Select(l => l.Folder).Distinct().Count());

        // The service never lets this nest pass on what came in through a link.
        var list = await (await RelayAsync(HttpMethod.Get, "/api/relay/links")).Content.ReadFromJsonAsync<LinkEndpoints.LinkList>(PairnetsJson.Options);
        Assert.Contains(Folder, list!.LinkedIn);

        Assert.Equal(HttpStatusCode.BadRequest, (await RelayAsync(HttpMethod.Post, "/api/relay/links", new { id = "nope" })).StatusCode);
    }

    [Fact]
    public async Task AnEncryptedNestCantJoin()
    {
        _anna.Store.Manifest.TurnOnSealed();
        var refused = await LinkAsync();
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("sealed", await refused.Content.ReadAsStringAsync());
    }
}
