using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Crypto;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// WebDAV links (docs/WEBDAV.md) against the real server: the password is its own revocable key that opens /dav and nothing
/// else; wrong, removed and foreign keys get 401; failures are slowed down; writes go through the store (manifest, history,
/// base check, push); 25 MB is the most one request may put; view-only and shared-folder links stay in their lane; no path
/// reaches outside the nest's files; an encrypted nest is refused.
/// </summary>
public sealed class WebDavTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _owner = _server.RawHttp(_server.Token);
    }

    public async Task DisposeAsync()
    {
        _owner.Dispose();
        await _server.DisposeAsync();
    }

    private sealed record Made(string Id, string Password, string Username, HttpClient Http);

    private async Task<Made> LinkAsync(string label = "Mac mini", string access = "edit", string[]? folders = null)
    {
        var resp = await _owner.PostAsJsonAsync("api/webdav/links", new { label, access, folders }, PairnetsJson.Options);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var password = doc.RootElement.GetProperty("password").GetString()!;
        return new Made(doc.RootElement.GetProperty("id").GetString()!, password, doc.RootElement.GetProperty("username").GetString()!, Dav(doc.RootElement.GetProperty("username").GetString()!, password));
    }

    private HttpClient Dav(string user, string password)
    {
        var http = new HttpClient { BaseAddress = _server.Url };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        return http;
    }

    private static HttpRequestMessage Req(string method, string url, HttpContent? content = null, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url) { Content = content };
        foreach (var (name, value) in headers)
            req.Headers.TryAddWithoutValidation(name, value);
        return req;
    }

    private async Task PutOwnerAsync(string path, string text) =>
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsync($"api/file?path={Uri.EscapeDataString(path)}&base=none", new StringContent(text))).StatusCode);

    private static async Task<string> BodyAsync(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    private ManifestEntry? Entry(string path) => _server.Store.Manifest.Get(path);

    // ------------------------------------------------------------------ the link and its password

    [Fact]
    public async Task ALinkIsMadeOnceWithAPasswordThatIsNeverListedAgain()
    {
        var made = await LinkAsync("Mac mini");
        Assert.Matches("^[a-hjkmnp-z2-9]{5}(-[a-hjkmnp-z2-9]{5}){3}$", made.Password);
        Assert.Equal("mac-mini", made.Username);

        var list = await _owner.GetStringAsync("api/webdav/links");
        Assert.DoesNotContain(made.Password, list);
        Assert.DoesNotContain(made.Password.Replace("-", ""), list);
        using var doc = JsonDocument.Parse(list);
        Assert.False(doc.RootElement.GetProperty("sealed").GetBoolean());
        Assert.Equal(DavEndpoints.MaxUploadBytes, doc.RootElement.GetProperty("maxUploadBytes").GetInt64());
        Assert.Equal(made.Id, doc.RootElement.GetProperty("links")[0].GetProperty("id").GetString());

        // stored as a hash only, and not in the computers' list
        var auth = _server.Services.GetRequiredService<AuthStore>();
        Assert.Empty(auth.ListDevices());
        Assert.Single(auth.ListWebDavLinks());
        Assert.DoesNotContain("Mac mini", await _owner.GetStringAsync("api/devices"));
    }

    [Fact]
    public async Task ANestKeepsAtMostTenLinks()
    {
        for (var i = 0; i < AuthStore.MaxWebDavLinks; i++)
            await LinkAsync("Link " + i);
        var resp = await _owner.PostAsJsonAsync("api/webdav/links", new { label = "One too many", access = "view" }, PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("too_many_links", (await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))!.Code);
    }

    [Theory]
    [InlineData("""{"label":"x"}""")]
    [InlineData("""{"label":"x","access":"owner"}""")]
    [InlineData("""{"label":"x","access":"view","folders":["a/b"]}""")]
    [InlineData("""{"access":"view"}""")]
    public async Task ABadLinkRequestIsRefused(string json)
    {
        var resp = await _owner.PostAsync("api/webdav/links", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task OnlyTheOwnerMakesListsAndRemovesLinks()
    {
        var friend = _server.Services.GetRequiredService<AuthStore>().AddDevice("Anna's PC", "test", "test", new DeviceAccess(DeviceAccess.Edit), "Anna", "acc_anna").Key;
        using var http = _server.RawHttp(friend);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/webdav/links")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("api/webdav/links", new { label = "x", access = "edit" }, PairnetsJson.Options)).StatusCode);
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.DeleteAsync("api/webdav/links/" + made.Id)).StatusCode);
    }

    [Fact]
    public async Task TheComputerCallsDoNotTouchALink()
    {
        var auth = _server.Services.GetRequiredService<AuthStore>();
        var made = await LinkAsync("Mac mini");

        Assert.Equal(HttpStatusCode.NotFound, (await _owner.DeleteAsync("api/devices/" + made.Id)).StatusCode);
        Assert.Single(auth.ListWebDavLinks());
    }

    // ------------------------------------------------------------------ keeping the wrong people out

    [Fact]
    public async Task NoPasswordAWrongPasswordAndEveryOtherKeyGet401()
    {
        var made = await LinkAsync();
        var computer = _server.MintKey("PC-1").Key;
        var attempts = new List<HttpClient>
        {
            new() { BaseAddress = _server.Url }, // no credentials
            Dav("x", "abcde-fghjk-mnpqr-stuvw"), // a well-formed password nobody has
            Dav("x", "wrong"),
            Dav(made.Username, computer), // a computer's key
            Dav(made.Username, _server.Token), // the shared token
        };
        foreach (var http in attempts)
        {
            var resp = await http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Contains("Basic", resp.Headers.WwwAuthenticate.Select(h => h.Scheme));
        }
        // and a computer's key in the ordinary header does not open /dav either
        using var plain = _server.RawHttp(computer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);
        using var token = _server.RawHttp(_server.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await token.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);
    }

    [Fact]
    public async Task ALinksPasswordOpensDavAndNothingElse()
    {
        var made = await LinkAsync(access: "edit");
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);

        var key = AuthStore.WebDavKeyOf(made.Password)!;
        foreach (var path in new[] { "api/manifest", "api/info", "api/devices", "api/webdav/links", "hub/negotiate?negotiateVersion=1" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await made.Http.GetAsync(path)).StatusCode);
            using var viaHeader = _server.RawHttp(key); // even the stored form of the key, sent the way computers send theirs
            Assert.Equal(HttpStatusCode.Unauthorized, (await viaHeader.GetAsync(path)).StatusCode);
            using var viaBearer = new HttpClient { BaseAddress = _server.Url };
            viaBearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            Assert.Equal(HttpStatusCode.Unauthorized, (await viaBearer.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task ARemovedLinkStopsAtOnceAndStaysStopped()
    {
        var made = await LinkAsync();
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode); // cached by now
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync("api/webdav/links/" + made.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await made.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.DeleteAsync("api/webdav/links/" + made.Id)).StatusCode);
        Assert.Empty(_server.Services.GetRequiredService<AuthStore>().ListWebDavLinks());
        // after a restart it is still gone
        await _server.RestartAsync();
        using var again = Dav(made.Username, made.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);
    }

    [Fact]
    public async Task APasswordTypedWithoutDashesOrInCapitalsStillWorks()
    {
        var made = await LinkAsync();
        using var typed = Dav("whatever", made.Password.Replace("-", "").ToUpperInvariant());
        Assert.Equal((HttpStatusCode)207, (await typed.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsAreSlowedDownAndARightOneClearsIt()
    {
        var made = await LinkAsync();
        using var wrong = Dav("x", "abcde-fghjk-mnpqr-stuvw");
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.SendAsync(Req("OPTIONS", "dav/"))).StatusCode);
        var watch = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.SendAsync(Req("OPTIONS", "dav/"))).StatusCode);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), $"the sixth failure was not slowed down ({watch.Elapsed})");
        // the same address with the right password is let in (after the delay) and the counter starts again
        Assert.Equal(HttpStatusCode.OK, (await made.Http.SendAsync(Req("OPTIONS", "dav/"))).StatusCode);
        watch.Restart();
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.SendAsync(Req("OPTIONS", "dav/"))).StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(900));
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task PropfindListsFoldersAndFilesAndRefusesInfiniteDepth()
    {
        await PutOwnerAsync("readme.txt", "hello");
        await PutOwnerAsync("Photos/beach.jpg", "jpeg bytes");
        await PutOwnerAsync("Photos/Trips/rome.txt", "rome");
        var made = await LinkAsync();

        var root = await made.Http.SendAsync(Req("PROPFIND", "dav/", new StringContent("""<?xml version="1.0"?><propfind xmlns="DAV:"><allprop/></propfind>"""), ("Depth", "1")));
        Assert.Equal((HttpStatusCode)207, root.StatusCode);
        var xml = await BodyAsync(root);
        Assert.Contains("<D:href>/dav/</D:href>", xml);
        Assert.Contains("<D:href>/dav/readme.txt</D:href>", xml);
        Assert.Contains("<D:href>/dav/Photos/</D:href>", xml);
        Assert.DoesNotContain("beach.jpg", xml); // one level only
        Assert.Contains("<D:getcontentlength>5</D:getcontentlength>", xml);
        Assert.Contains("<D:getetag>\"" + Entry("readme.txt")!.Hash + "\"</D:getetag>", xml);

        var photos = await BodyAsync(await made.Http.SendAsync(Req("PROPFIND", "dav/Photos", null, ("Depth", "1"))));
        Assert.Contains("<D:href>/dav/Photos/beach.jpg</D:href>", photos);
        Assert.Contains("<D:href>/dav/Photos/Trips/</D:href>", photos);

        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("PROPFIND", "dav/"))).StatusCode); // no Depth means infinity
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "infinity")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Req("PROPFIND", "dav/nothing", null, ("Depth", "0")))).StatusCode);
    }

    [Fact]
    public async Task AFileIsReadWholeAndInPartsAndNamesWithSpacesAndLettersWork()
    {
        await PutOwnerAsync("My Trip/Café notes.txt", "0123456789");
        var made = await LinkAsync();
        var url = "dav/My%20Trip/Caf%C3%A9%20notes.txt";
        Assert.Equal("0123456789", await made.Http.GetStringAsync(url));
        var part = await made.Http.SendAsync(Req("GET", url, null, ("Range", "bytes=2-4")));
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("234", await BodyAsync(part));
        var head = await made.Http.SendAsync(Req("HEAD", url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(10, head.Content.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.GetAsync("dav/My%20Trip/none.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await made.Http.GetAsync("dav/My%20Trip/")).StatusCode);
    }

    [Fact]
    public async Task FilesOverTwentyFiveMegabytesCanStillBeRead()
    {
        var big = new byte[DavEndpoints.MaxUploadBytes + 1024];
        Random.Shared.NextBytes(big);
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsync("api/file?path=big.bin&base=none", new ByteArrayContent(big))).StatusCode);
        var made = await LinkAsync();
        Assert.Equal(big, await made.Http.GetByteArrayAsync("dav/big.bin"));
    }

    // ------------------------------------------------------------------ writing (through the store)

    [Fact]
    public async Task APutMakesTheFileInTheManifestAndAnOverwriteKeepsTheOldVersion()
    {
        var made = await LinkAsync();
        var created = await made.Http.PutAsync("dav/Docs/a.txt", new StringContent("one"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = Entry("Docs/a.txt")!;
        Assert.Equal(3, first.Size);
        Assert.Equal("\"" + first.Hash + "\"", created.Headers.ETag?.Tag);
        Assert.Equal("one", await _owner.GetStringAsync("api/file?path=Docs%2Fa.txt"));

        var changed = await made.Http.PutAsync("dav/Docs/a.txt", new StringContent("two!"));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.NotEqual(first.Hash, Entry("Docs/a.txt")!.Hash);
        Assert.True(Entry("Docs/a.txt")!.Version > first.Version);
        var history = await _owner.GetStringAsync("api/history?path=Docs%2Fa.txt");
        Assert.Contains(first.Hash![..8], history); // the version it replaced is in the nest's history
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.PutAsync("dav/Docs/a.txt", new StringContent("two!"))).StatusCode); // same content: nothing new
    }

    [Fact]
    public async Task APutIsHeardOnThePushChannelLikeAnAppsUpload()
    {
        var computer = _server.MintKey("PC-1").Key;
        await using var listener = new FriendAccessTests.Listener(new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(computer)).Build());
        await listener.Hub.StartAsync();
        await listener.Hub.InvokeAsync(nameof(SyncHub.Synced), 0L);
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/pushed.txt", new StringContent("x"))).StatusCode);
        await listener.WaitForAsync("pushed.txt");
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/pushed.txt")).StatusCode);
        await listener.WaitForAsync("pushed.txt");
    }

    [Fact]
    public async Task IfMatchAndIfNoneMatchKeepTwoWritersApart()
    {
        await PutOwnerAsync("doc.txt", "v1");
        var made = await LinkAsync();
        var v1 = Entry("doc.txt")!.Hash!;

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await made.Http.SendAsync(Req("PUT", "dav/doc.txt", new StringContent("mine"), ("If-None-Match", "*")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("PUT", "dav/fresh.txt", new StringContent("mine"), ("If-None-Match", "*")))).StatusCode);

        // another computer changes the file; a save that started from v1 is refused and the other change survives
        Assert.Equal(HttpStatusCode.OK, (await _owner.PutAsync($"api/file?path=doc.txt&base={v1}", new StringContent("theirs"))).StatusCode);
        var stale = await made.Http.SendAsync(Req("PUT", "dav/doc.txt", new StringContent("mine"), ("If-Match", "\"" + v1 + "\"")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("theirs", await _owner.GetStringAsync("api/file?path=doc.txt"));
        var now = Entry("doc.txt")!.Hash!;
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.SendAsync(Req("PUT", "dav/doc.txt", new StringContent("merged"), ("If-Match", "\"" + now + "\"")))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await made.Http.SendAsync(Req("PUT", "dav/doc.txt", new StringContent("x"), ("If-Match", "\"nonsense\"")))).StatusCode);
    }

    [Fact]
    public async Task AFileOverTwentyFiveMegabytesGets413AndNothingIsKept()
    {
        var made = await LinkAsync();
        var over = new byte[DavEndpoints.MaxUploadBytes + 1];

        var withLength = await made.Http.PutAsync("dav/over.bin", new ByteArrayContent(over));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, withLength.StatusCode);
        Assert.Contains("25 MB", await BodyAsync(withLength));
        Assert.Null(Entry("over.bin"));

        // a client that does not say how long the file is (chunked) is stopped at the limit too
        var chunked = new HttpRequestMessage(HttpMethod.Put, "dav/chunked.bin") { Content = new StreamContent(new MemoryStream(over)) };
        chunked.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await made.Http.SendAsync(chunked)).StatusCode);
        Assert.Null(Entry("chunked.bin"));
        Assert.Empty(Directory.EnumerateFiles(_server.Paths.Tmp));

        // exactly 25 MB goes in
        var exact = new byte[DavEndpoints.MaxUploadBytes];
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/exact.bin", new ByteArrayContent(exact))).StatusCode);
        Assert.Equal(DavEndpoints.MaxUploadBytes, Entry("exact.bin")!.Size);
    }

    [Fact]
    public async Task DeleteMakesATombstoneAndKeepsTheVersionInHistory()
    {
        await PutOwnerAsync("Docs/a.txt", "keep me");
        await PutOwnerAsync("Docs/b.txt", "and me");
        var made = await LinkAsync();
        var hash = Entry("Docs/a.txt")!.Hash!;
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/Docs/a.txt")).StatusCode);
        Assert.True(Entry("Docs/a.txt")!.Deleted);
        Assert.Contains(hash[..8], await _owner.GetStringAsync("api/history?path=Docs%2Fa.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.DeleteAsync("dav/Docs/a.txt")).StatusCode); // already gone

        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/Docs")).StatusCode); // the rest of the folder
        Assert.True(Entry("Docs/b.txt")!.Deleted);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.DeleteAsync("dav/Docs")).StatusCode);
    }

    [Fact]
    public async Task ABigFolderIsNotDeletedOrMovedInOneRequest()
    {
        for (var i = 0; i <= DavEndpoints.MaxFilesPerFolderCall; i++)
            await PutOwnerAsync($"Many/f{i}.txt", "x");
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.DeleteAsync("dav/Many")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MOVE", "dav/Many", null, ("Destination", "/dav/Moved")))).StatusCode);
        Assert.False(Entry("Many/f0.txt")!.Deleted);
        Assert.Null(Entry("Moved/f0.txt"));
    }

    [Fact]
    public async Task MkcolMakesAFolderThatShowsUpAndNeedsItsParent()
    {
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MKCOL", "dav/New"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await made.Http.SendAsync(Req("MKCOL", "dav/New"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await made.Http.SendAsync(Req("MKCOL", "dav/Missing/Child"))).StatusCode);
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Req("PROPFIND", "dav/New", null, ("Depth", "0")))).StatusCode);
        Assert.Contains("<D:href>/dav/New/</D:href>", await BodyAsync(await made.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "1")))));
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/New/in.txt", new StringContent("x"))).StatusCode);
        Assert.NotNull(Entry("New/in.txt"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await made.Http.SendAsync(Req("MKCOL", "dav/Body", new StringContent("x")))).StatusCode);
    }

    [Fact]
    public async Task MoveRenamesAFileAndAFolderAndHonoursOverwrite()
    {
        await PutOwnerAsync("a.txt", "A");
        await PutOwnerAsync("b.txt", "B");
        await PutOwnerAsync("Old/x.txt", "X");
        await PutOwnerAsync("Old/y.txt", "Y");
        var made = await LinkAsync();

        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/a.txt", null, ("Destination", "http://localhost/dav/renamed.txt")))).StatusCode);
        Assert.True(Entry("a.txt")!.Deleted);
        Assert.Equal("A", await _owner.GetStringAsync("api/file?path=renamed.txt"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await made.Http.SendAsync(Req("MOVE", "dav/renamed.txt", null, ("Destination", "/dav/b.txt"), ("Overwrite", "F")))).StatusCode);
        var before = _server.Store.ReadManifest(null).Version; // the nest's change counter
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.SendAsync(Req("MOVE", "dav/renamed.txt", null, ("Destination", "/dav/b.txt"), ("Overwrite", "T")))).StatusCode);
        Assert.Equal("A", await _owner.GetStringAsync("api/file?path=b.txt"));
        Assert.Equal(before + 1, Entry("b.txt")!.Version); // b.txt was the move's first change: replaced in one step, never deleted first

        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/Old", null, ("Destination", "/dav/New%20name")))).StatusCode);
        Assert.Equal("X", await _owner.GetStringAsync("api/file?path=New%20name%2Fx.txt"));
        Assert.True(Entry("Old/y.txt")!.Deleted);

        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MOVE", "dav/b.txt", null, ("Destination", "/dav/b.txt")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MOVE", "dav/New%20name", null, ("Destination", "/dav/New%20name/inside")))).StatusCode);
    }

    [Fact]
    public async Task LockingIsAFormalityForLinksThatCanChangeAndRefusedForViewOnly()
    {
        var edit = await LinkAsync("Editor", "edit");
        var view = await LinkAsync("Viewer", "view");

        var options = await edit.Http.SendAsync(Req("OPTIONS", "dav/"));
        Assert.Equal("1, 2", options.Headers.GetValues("DAV").Single());
        Assert.Contains("MKCOL", options.Content.Headers.Allow);
        var locked = await edit.Http.SendAsync(Req("LOCK", "dav/new.txt", new StringContent("""<D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockinfo>""")));
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.StartsWith("<opaquelocktoken:", locked.Headers.GetValues("Lock-Token").Single());
        Assert.Equal(HttpStatusCode.NoContent, (await edit.Http.SendAsync(Req("UNLOCK", "dav/new.txt", null, ("Lock-Token", "<opaquelocktoken:x>")))).StatusCode);

        var viewOptions = await view.Http.SendAsync(Req("OPTIONS", "dav/"));
        Assert.Equal("1", viewOptions.Headers.GetValues("DAV").Single());
        Assert.DoesNotContain("PUT", viewOptions.Content.Headers.Allow);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.Http.SendAsync(Req("LOCK", "dav/new.txt", new StringContent("")))).StatusCode);
    }

    [Fact]
    public async Task MacJunkFilesAreAcceptedAndDropped()
    {
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/._notes.txt", new StringContent("apple double"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/.DS_Store", new StringContent("x"))).StatusCode);
        Assert.Null(Entry("._notes.txt"));
        Assert.Null(Entry(".DS_Store"));
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.GetAsync("dav/._notes.txt")).StatusCode);

        // metadata never takes the place of a real file, and a rename onto a metadata name is refused rather than "done"
        await PutOwnerAsync("real.txt", "real");
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Req("MOVE", "dav/._notes.txt", null, ("Destination", "/dav/real.txt")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MOVE", "dav/real.txt", null, ("Destination", "/dav/.DS_Store")))).StatusCode);
        Assert.Equal("real", await _owner.GetStringAsync("api/file?path=real.txt"));
    }

    [Fact]
    public async Task AWordStyleSaveThroughTempFilesKeepsTheNewContent()
    {
        // How Word saves on a mapped drive: write a temp file, move the document aside, move the temp file onto it, delete the old one.
        await PutOwnerAsync("Docs/report.docx", "old");
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/Docs/~$report.docx", new StringContent("owner file"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/Docs/~WRD0000.tmp", new StringContent("new"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/Docs/report.docx", null, ("Destination", "/dav/Docs/~WRL0001.tmp")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/Docs/~WRD0000.tmp", null, ("Destination", "/dav/Docs/report.docx")))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/Docs/~WRL0001.tmp")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/Docs/~$report.docx")).StatusCode);

        Assert.Equal("new", await _owner.GetStringAsync("api/file?path=Docs%2Freport.docx"));
        Assert.Equal("new", await made.Http.GetStringAsync("dav/Docs/report.docx"));
        Assert.DoesNotContain(_server.Store.ReadManifest(null).Entries, e => !e.Deleted && e.Path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ staying in one's lane

    [Fact]
    public async Task AViewOnlyLinkChangesNothing()
    {
        await PutOwnerAsync("doc.txt", "original");
        var made = await LinkAsync("Viewer", "view");
        Assert.Equal("original", await made.Http.GetStringAsync("dav/doc.txt"));
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.PutAsync("dav/doc.txt", new StringContent("changed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.PutAsync("dav/new.txt", new StringContent("new"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.DeleteAsync("dav/doc.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MKCOL", "dav/Folder"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MOVE", "dav/doc.txt", null, ("Destination", "/dav/other.txt")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("PROPPATCH", "dav/doc.txt", new StringContent("")))).StatusCode);
        Assert.Equal("original", await _owner.GetStringAsync("api/file?path=doc.txt"));
        Assert.Null(Entry("new.txt"));
    }

    [Fact]
    public async Task ALinkForSomeFoldersSeesAndTouchesOnlyThose()
    {
        await PutOwnerAsync("Photos/beach.jpg", "beach");
        await PutOwnerAsync("Taxes/2026.pdf", "secret");
        await PutOwnerAsync("readme.txt", "root");
        var made = await LinkAsync("Photos only", "edit", ["Photos"]);

        var root = await BodyAsync(await made.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "1"))));
        Assert.Contains("/dav/Photos/", root);
        Assert.DoesNotContain("Taxes", root);
        Assert.DoesNotContain("readme", root);
        Assert.Equal("beach", await made.Http.GetStringAsync("dav/Photos/beach.jpg"));
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.GetAsync("dav/Taxes/2026.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Req("PROPFIND", "dav/Taxes", null, ("Depth", "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.PutAsync("dav/Taxes/new.txt", new StringContent("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.PutAsync("dav/top.txt", new StringContent("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.DeleteAsync("dav/Taxes/2026.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MOVE", "dav/Photos/beach.jpg", null, ("Destination", "/dav/Taxes/beach.jpg")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await made.Http.SendAsync(Req("MOVE", "dav/Taxes/2026.pdf", null, ("Destination", "/dav/Photos/2026.pdf")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/Photos/new.jpg", new StringContent("x"))).StatusCode);
        Assert.Equal("secret", await _owner.GetStringAsync("api/file?path=Taxes%2F2026.pdf"));
    }

    [Theory]
    [InlineData("dav/%2e%2e/escaped.txt")]
    [InlineData("dav/%2E%2E%2Fescaped.txt")]
    [InlineData("dav/a/..%2F..%2Fescaped.txt")]
    [InlineData("dav/..%5Cescaped.txt")]
    [InlineData("dav/a%5C..%5C..%5Cescaped.txt")]
    [InlineData("dav/C:%5Cescaped.txt")]
    [InlineData("dav/%2e/%2e/escaped.txt")]
    [InlineData("dav/a%00b.txt")]
    public async Task NoPathReachesOutsideTheNestsFiles(string url)
    {
        var made = await LinkAsync();
        var parent = Directory.GetParent(_server.DataDir)!.FullName;
        var before = Directory.EnumerateFiles(parent, "escaped*", SearchOption.AllDirectories).Count();

        // Sent exactly as written: .NET's Uri would otherwise turn "%2e/%2e" into nothing before the request leaves.
        var raw = new Uri(_server.Url.GetLeftPart(UriPartial.Authority) + "/" + url, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        foreach (var method in new[] { "PUT", "GET", "DELETE", "MKCOL", "PROPFIND", "MOVE" })
        {
            var req = new HttpRequestMessage(new HttpMethod(method), raw) { Content = method == "PUT" ? new StringContent("x") : null };
            req.Headers.TryAddWithoutValidation("Depth", "1");
            req.Headers.TryAddWithoutValidation("Destination", "/dav/ok.txt");
            var resp = await made.Http.SendAsync(req);
            Assert.True(resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.Conflict or HttpStatusCode.Unauthorized, $"{method} {url}: {(int)resp.StatusCode}");
        }
        Assert.Equal(before, Directory.EnumerateFiles(parent, "escaped*", SearchOption.AllDirectories).Count());
        Assert.DoesNotContain(_server.Store.ReadManifest(null).Entries, e => e.Path.Contains("escaped", StringComparison.Ordinal) && !e.Deleted);
    }

    [Theory]
    [InlineData("/dav/../escaped.txt")]
    [InlineData("/dav/%2e%2e/escaped.txt")]
    [InlineData("/api/file?path=escaped.txt")]
    [InlineData("/other/escaped.txt")]
    [InlineData("http://evil.example/dav/escaped.txt")]
    [InlineData("/dav%5C..%5Cescaped.txt")]
    [InlineData("escaped.txt")]
    [InlineData("/dav/a%5Cb.txt")]
    public async Task ADestinationOutsideTheNestIsRefused(string destination)
    {
        await PutOwnerAsync("src.txt", "source");
        var made = await LinkAsync();
        var resp = await made.Http.SendAsync(Req("MOVE", "dav/src.txt", null, ("Destination", destination)));
        Assert.True(resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, $"{destination}: {(int)resp.StatusCode}");
        Assert.False(Entry("src.txt")!.Deleted);
        Assert.Null(Entry("escaped.txt"));
        Assert.DoesNotContain(_server.Store.ReadManifest(null).Entries, e => e.Path.Contains("escaped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEncryptedNestRefusesLinksAndDav()
    {
        var made = await LinkAsync(); // made while the nest was still plain
        using var keys = ComputerKeys.Create();
        var ring = KeyRing.Create(_server.Store.ServerId, keys.AsTrusted("PC-1"));
        using var http = _server.RawHttp(_server.Token);
        http.DefaultRequestHeaders.Add(PairnetsHeaders.Features, PairnetsHeaders.SealedFeature);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsync("api/keys/recovery", new ByteArrayContent(Envelope.SealForRecovery(ring, RecoveryKey.Create())))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsync($"api/keys/envelopes/{keys.Id}", new ByteArrayContent(Envelope.SealForComputer(ring, keys.AgreementPublicKey, keys)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("api/keys/seal", null)).StatusCode);
        Assert.True(_server.Store.Sealed);

        var newLink = await _owner.PostAsJsonAsync("api/webdav/links", new { label = "Late", access = "view" }, PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, newLink.StatusCode);
        Assert.Equal("sealed", (await newLink.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))!.Code);

        foreach (var method in new[] { "PROPFIND", "GET", "PUT", "OPTIONS" })
        {
            var resp = await made.Http.SendAsync(Req(method, "dav/", method == "PUT" ? new StringContent("x") : null, ("Depth", "1")));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.Contains("encrypted", await BodyAsync(resp));
        }
    }

    [Fact]
    public async Task InRelayModeOnlyTheServiceSetsThePublicBaseOfTheLinks()
    {
        await PutOwnerAsync("a.txt", "A");
        var made = await LinkAsync();
        // not in relay mode: a client-sent base is ignored, hrefs stay under /dav
        var xml = await BodyAsync(await made.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "1"), ("X-Pairnets-Dav-Base", "https://pairnets.app/evil.nst_x"))));
        Assert.Contains("<D:href>/dav/a.txt</D:href>", xml);
        Assert.DoesNotContain("evil", xml);
    }
}
