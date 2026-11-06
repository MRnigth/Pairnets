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
using Pairnets.Server.Cli;
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

    private HttpClient Dav(string user, string password, TestServer? server = null)
    {
        var http = new HttpClient { BaseAddress = (server ?? _server).Url };
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

    private List<string> Live() => _server.Store.ReadManifest(null).Entries.Where(e => !e.Deleted).Select(e => e.Path).Order(StringComparer.Ordinal).ToList();

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

    private async Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliCommands.RunAsync([.. args, "--data-dir", _server.DataDir], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task DevicesListShowsLinksInASectionOfTheirOwnAndDevicesRemoveSaysItRemovedALink()
    {
        var auth = _server.Services.GetRequiredService<AuthStore>();
        var computer = _server.MintKey("DESK");
        var photos = await LinkAsync("Mac mini", "view", ["Photos", "Work"]);
        var desk = await LinkAsync("DESK", "edit"); // a link may share a computer's name
        Assert.Equal((HttpStatusCode)207, (await photos.Http.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);

        var (code, output, error) = await CliAsync("devices", "list");
        Assert.True(code == 0, error);
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        var linksAt = Array.FindIndex(lines, l => l.StartsWith("WebDAV links (2;", StringComparison.Ordinal));
        Assert.True(linksAt > Array.FindIndex(lines, l => l.StartsWith("Shared token:", StringComparison.Ordinal)), output);
        var computers = lines[..linksAt];
        var links = lines[(linksAt + 1)..];
        Assert.Contains(computers, l => l.StartsWith(computer.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(computers, l => l.Contains(photos.Id, StringComparison.Ordinal) || l.Contains(desk.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(links, l => l.Contains(computer.Id, StringComparison.Ordinal));
        Assert.Contains(links, l => l.StartsWith(photos.Id, StringComparison.Ordinal) && l.Contains("Mac mini", StringComparison.Ordinal)
            && l.Contains("view only", StringComparison.Ordinal) && l.Contains("folders: Photos, Work", StringComparison.Ordinal)
            && l.Contains($"made {DateTime.UtcNow:yyyy-MM-dd}", StringComparison.Ordinal) && l.Contains("last used ", StringComparison.Ordinal) && l.Contains(" UTC", StringComparison.Ordinal));
        Assert.Contains(links, l => l.StartsWith(desk.Id, StringComparison.Ordinal) && l.Contains("can change", StringComparison.Ordinal)
            && l.Contains("whole nest", StringComparison.Ordinal) && l.Contains("never used", StringComparison.Ordinal));
        foreach (var password in new[] { photos.Password, desk.Password })
        {
            var hash = AuthStore.HashKey(AuthStore.WebDavKeyOf(password)!);
            Assert.DoesNotContain(password.Replace("-", string.Empty, StringComparison.Ordinal), output.Replace("-", string.Empty, StringComparison.Ordinal));
            Assert.DoesNotContain(Convert.ToHexString(hash), output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(hash).TrimEnd('='), output, StringComparison.Ordinal);
        }

        // "DESK" names a computer and a link: the id decides. Otherwise the name does, and the answer says a link went.
        var (both, _, why) = await CliAsync("devices", "remove", "desk");
        Assert.Equal(1, both);
        Assert.Contains("both called 'desk'", why);
        Assert.True(auth.GetDevice(computer.Id)!.IsActive && auth.GetDevice(desk.Id)!.IsActive);
        var (byName, saidByName, _) = await CliAsync("devices", "remove", "mac mini");
        Assert.Equal((0, "Removed the WebDAV link Mac mini. Its password stops working within 30 seconds; files already copied with it stay where they are."),
            (byName, saidByName.Trim()));
        var (byId, saidById, _) = await CliAsync("devices", "remove", desk.Id);
        Assert.True(byId == 0 && saidById.StartsWith("Removed the WebDAV link DESK.", StringComparison.Ordinal), saidById);
        var (pc, saidPc, _) = await CliAsync("devices", "remove", "DESK");
        Assert.True(pc == 0 && saidPc.StartsWith("Removed DESK. Its key stops", StringComparison.Ordinal), saidPc);
        Assert.Empty(auth.ListWebDavLinks());
        Assert.Contains("WebDAV links: none.", (await CliAsync("devices", "list")).Output);

        // The command line is another process: the running nest drops the password within its key cache's 30 seconds, a restart at once.
        await _server.RestartAsync();
        using var again = Dav(photos.Username, photos.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.SendAsync(Req("PROPFIND", "dav/", null, ("Depth", "0")))).StatusCode);
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
    public async Task ARenameThatOnlyChangesLetterCaseWorksForAFile()
    {
        await PutOwnerAsync("Docs/photo.jpg", "jpeg");
        await PutOwnerAsync("Docs/other.txt", "o");
        var made = await LinkAsync();
        var old = Entry("Docs/photo.jpg")!;

        // Finder's rename: Overwrite F, as the new name is "free"
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/Docs/photo.jpg", null, ("Destination", "/dav/Docs/Photo.jpg"), ("Overwrite", "F")))).StatusCode);
        Assert.True(Entry("Docs/photo.jpg")!.Deleted); // computers see the old name deleted and the new one added
        var renamed = Entry("Docs/Photo.jpg")!;
        Assert.Equal(old.Hash, renamed.Hash);
        Assert.Equal(old.ModifiedMs, renamed.ModifiedMs);
        Assert.Equal("jpeg", await made.Http.GetStringAsync("dav/Docs/Photo.jpg"));
        Assert.Contains(old.Hash![..8], await _owner.GetStringAsync("api/history?path=Docs%2Fphoto.jpg")); // the old version is in history
        Assert.Equal(["Docs/Photo.jpg", "Docs/other.txt"], Live()); // one file, and no passing name left
        Assert.Equal(["Photo.jpg", "other.txt"], Directory.GetFiles(Path.Combine(_server.Paths.Files, "Docs")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("<D:href>/dav/Docs/Photo.jpg</D:href>", await BodyAsync(await made.Http.SendAsync(Req("PROPFIND", "dav/Docs", null, ("Depth", "1")))));

        // and back again
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/Docs/Photo.jpg", null, ("Destination", "/dav/Docs/photo.jpg")))).StatusCode);
        Assert.Equal(["Docs/other.txt", "Docs/photo.jpg"], Live());
        Assert.Equal("jpeg", await _owner.GetStringAsync("api/file?path=Docs%2Fphoto.jpg"));
    }

    [Fact]
    public async Task ARenameThatOnlyChangesLetterCaseWorksForAFolder()
    {
        await PutOwnerAsync("trip/a.txt", "A");
        await PutOwnerAsync("trip/b.txt", "B");
        await PutOwnerAsync("trip/Day 2/c.txt", "C");
        var made = await LinkAsync();
        var hashes = new[] { "trip/a.txt", "trip/b.txt", "trip/Day 2/c.txt" }.ToDictionary(p => p, p => Entry(p)!.Hash!);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/trip/new.txt", new ByteArrayContent([]))).StatusCode); // held, not stored yet
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MKCOL", "dav/trip/Empty"))).StatusCode); // a folder with nothing in it yet

        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/trip/", null, ("Destination", "/dav/Trip/")))).StatusCode);
        Assert.Equal(["Trip/Day 2/c.txt", "Trip/a.txt", "Trip/b.txt"], Live());
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Req("PROPFIND", "dav/Trip/new.txt", null, ("Depth", "0")))).StatusCode); // they follow the new name
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Req("PROPFIND", "dav/Trip/Empty", null, ("Depth", "0")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Req("PROPFIND", "dav/trip/new.txt", null, ("Depth", "0")))).StatusCode);
        foreach (var (path, hash) in hashes)
        {
            Assert.True(Entry(path)!.Deleted);
            Assert.Equal(hash, Entry("T" + path[1..])!.Hash);
            Assert.Contains(hash[..8], await _owner.GetStringAsync("api/history?path=" + Uri.EscapeDataString(path)));
        }
        Assert.Equal("C", await made.Http.GetStringAsync("dav/Trip/Day%202/c.txt"));
        Assert.Equal(["Trip"], Directory.GetDirectories(_server.Paths.Files).Select(Path.GetFileName)); // the nest's own folder follows too
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Req("PROPFIND", "dav/trip", null, ("Depth", "0")))).StatusCode);

        // only the last name may change its letter case in one go
        Assert.Equal(HttpStatusCode.Conflict, (await made.Http.SendAsync(Req("MOVE", "dav/Trip/a.txt", null, ("Destination", "/dav/trip/a.txt")))).StatusCode);
        Assert.Equal(["Trip/Day 2/c.txt", "Trip/a.txt", "Trip/b.txt"], Live());
    }

    [Fact]
    public async Task ARenameByLetterCaseThatFailsHalfwayPutsEverythingBack()
    {
        await PutOwnerAsync("trip/a.txt", "A");
        await PutOwnerAsync("trip/b.txt", "B");
        var made = await LinkAsync();
        await File.WriteAllTextAsync(Path.Combine(_server.Paths.Files, "trip", "b.txt"), "changed under the nest"); // b.txt can't be moved

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await made.Http.SendAsync(Req("MOVE", "dav/trip", null, ("Destination", "/dav/Trip")))).StatusCode);
        Assert.Equal(["trip/a.txt", "trip/b.txt"], Live()); // a.txt had gone to its passing name and is back
        Assert.Equal("A", await _owner.GetStringAsync("api/file?path=trip%2Fa.txt"));
        Assert.DoesNotContain(_server.Store.ReadManifest(null).Entries, e => !e.Deleted && e.Path.Contains(DavEndpoints.PassingMark, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AComputerFollowsARenameByLetterCaseWithOneFileUnderTheNewName()
    {
        await using var laptop = new Device("laptop", _server);
        laptop.Write("photo.jpg", "jpeg");
        laptop.Write("trip/a.txt", "A");
        laptop.Write("trip/b.txt", "B");
        await laptop.SyncUntilQuietAsync();
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/photo.jpg", null, ("Destination", "/dav/Photo.jpg")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/trip", null, ("Destination", "/dav/Trip")))).StatusCode);
        var version = _server.Store.ReadManifest(null).Version;

        // The pass sees "photo.jpg deleted, Photo.jpg added": it deletes the old one first, then downloads the new one.
        await laptop.SyncUntilQuietAsync();
        Assert.Equal(["Photo.jpg", "Trip/a.txt", "Trip/b.txt"], laptop.Files()); // as named on its disk (case-insensitive here)
        Assert.Equal("A", laptop.Read("Trip/a.txt"));
        Assert.Equal(version, _server.Store.ReadManifest(null).Version); // and it sends nothing back
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

    // ------------------------------------------------------------------ a new file made empty first (Finder, Explorer)

    private const string LockBody = """<D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockinfo>""";

    private const string HeldETag = "\"" + DavEmptyFiles.ETag + "\"";

    private static ByteArrayContent Empty() => new([]);

    private static HttpRequestMessage Find(string url, string depth = "0") => Req("PROPFIND", url, null, ("Depth", depth));

    private long Changes() => _server.Store.ReadManifest(null).Version;

    [Fact]
    public async Task AFinderStyleCopyHoldsTheEmptyFileAndStoresOnlyTheContent()
    {
        // How Finder and Explorer copy a file in: an empty PUT makes it, they look at it, lock it and set its times, then PUT the content.
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MKCOL", "dav/Copies"))).StatusCode);
        var before = Changes();

        var created = await made.Http.PutAsync("dav/Copies/photo.jpg", Empty());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HeldETag, created.Headers.ETag?.Tag);
        Assert.Null(Entry("Copies/photo.jpg")); // held, not stored: no version, no push
        Assert.Equal(before, Changes());

        var one = await BodyAsync(await made.Http.SendAsync(Find("dav/Copies/photo.jpg")));
        Assert.Contains("<D:href>/dav/Copies/photo.jpg</D:href>", one);
        Assert.Contains("<D:getcontentlength>0</D:getcontentlength>", one);
        Assert.Contains("<D:getetag>" + HeldETag + "</D:getetag>", one);
        Assert.Contains("<D:href>/dav/Copies/photo.jpg</D:href>", await BodyAsync(await made.Http.SendAsync(Find("dav/Copies/", "1"))));
        var head = await made.Http.SendAsync(Req("HEAD", "dav/Copies/photo.jpg"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(0, head.Content.Headers.ContentLength);
        Assert.Equal(HeldETag, head.Headers.ETag?.Tag);
        Assert.Equal(string.Empty, await made.Http.GetStringAsync("dav/Copies/photo.jpg"));
        var locked = await made.Http.SendAsync(Req("LOCK", "dav/Copies/photo.jpg", new StringContent(LockBody)));
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var token = locked.Headers.GetValues("Lock-Token").Single();
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Req("PROPPATCH", "dav/Copies/photo.jpg", new StringContent("""<?xml version="1.0"?><propertyupdate xmlns="DAV:"/>""")))).StatusCode);
        Assert.Null(Entry("Copies/photo.jpg"));

        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.SendAsync(Req("PUT", "dav/Copies/photo.jpg", new StringContent("jpeg bytes"), ("If", "(" + token + ")")))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.SendAsync(Req("UNLOCK", "dav/Copies/photo.jpg", null, ("Lock-Token", token)))).StatusCode);

        var stored = Entry("Copies/photo.jpg")!;
        Assert.Equal(10, stored.Size);
        Assert.Equal(before + 1, Changes()); // one change for the whole copy: the file with its content
        Assert.Equal(before + 1, stored.Version);
        Assert.Empty(_server.Store.ListHistory("Copies/photo.jpg")); // and no empty version before it
        Assert.Contains("<D:getetag>\"" + stored.Hash + "\"</D:getetag>", await BodyAsync(await made.Http.SendAsync(Find("dav/Copies/photo.jpg"))));
        Assert.Equal("jpeg bytes", await made.Http.GetStringAsync("dav/Copies/photo.jpg"));
    }

    [Fact]
    public async Task ABigFileAfterAnEmptyPutGets413AndLeavesNothingBehind()
    {
        await PutOwnerAsync("keep.bin", "the real one");
        var kept = Entry("keep.bin")!;
        var made = await LinkAsync();
        var before = Changes();
        var over = new byte[DavEndpoints.MaxUploadBytes + 1];

        // the content with its length: refused before it is read, and the held empty file goes with it
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/movie.mov", Empty())).StatusCode);
        var locked = await made.Http.SendAsync(Req("LOCK", "dav/movie.mov", new StringContent(LockBody)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await made.Http.PutAsync("dav/movie.mov", new ByteArrayContent(over))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.SendAsync(Req("UNLOCK", "dav/movie.mov", null, ("Lock-Token", locked.Headers.GetValues("Lock-Token").Single())))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Find("dav/movie.mov"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.GetAsync("dav/movie.mov")).StatusCode);
        Assert.DoesNotContain("movie.mov", await BodyAsync(await made.Http.SendAsync(Find("dav/", "1"))));

        // the content without a length (chunked): stopped at the limit, and the held file goes too
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/chunked.mov", Empty())).StatusCode);
        var chunked = new HttpRequestMessage(HttpMethod.Put, "dav/chunked.mov") { Content = new StreamContent(new MemoryStream(over)) };
        chunked.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await made.Http.SendAsync(chunked)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Find("dav/chunked.mov"))).StatusCode);

        // a stored file of that name stays as it was
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await made.Http.PutAsync("dav/keep.bin", new ByteArrayContent(over))).StatusCode);

        Assert.Null(Entry("movie.mov"));
        Assert.Null(Entry("chunked.mov"));
        Assert.Equal(kept, Entry("keep.bin"));
        Assert.Equal("the real one", await _owner.GetStringAsync("api/file?path=keep.bin"));
        Assert.Empty(_server.Store.ListHistory("keep.bin"));
        Assert.Equal(before, Changes()); // nothing at all was stored, so no computer heard of any of it
        Assert.Equal(0, _server.Services.GetRequiredService<DavEmptyFiles>().Count); // and nothing is left to be stored empty later
        Assert.Empty(Directory.EnumerateFiles(_server.Paths.Tmp));
    }

    [Fact]
    public async Task AnEmptyPutOverAStoredFileEmptiesItAsBefore()
    {
        await PutOwnerAsync("notes.txt", "some notes");
        var old = Entry("notes.txt")!;
        var made = await LinkAsync();

        var emptied = await made.Http.PutAsync("dav/notes.txt", Empty());
        Assert.Equal(HttpStatusCode.NoContent, emptied.StatusCode);
        var now = Entry("notes.txt")!;
        Assert.Equal(0, now.Size);
        Assert.True(now.Version > old.Version);
        Assert.Equal("\"" + now.Hash + "\"", emptied.Headers.ETag?.Tag);
        Assert.Contains(_server.Store.ListHistory("notes.txt"), h => h.Hash8 == old.Hash![..8]); // the old content is one click away
        Assert.Equal(string.Empty, await _owner.GetStringAsync("api/file?path=notes.txt"));
    }

    [Fact]
    public async Task AHeldEmptyFileIsRenamedAndDeletedLikeAFile()
    {
        await PutOwnerAsync("taken.txt", "taken");
        var made = await LinkAsync();

        // Explorer's "New > Text Document": made empty, renamed, then written by Notepad
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/New%20Text%20Document.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await made.Http.SendAsync(Req("MKCOL", "dav/New%20Text%20Document.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await made.Http.SendAsync(Req("PUT", "dav/New%20Text%20Document.txt", Empty(), ("If-None-Match", "*")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/New%20Text%20Document.txt", null, ("Destination", "/dav/notes.txt")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Find("dav/New%20Text%20Document.txt"))).StatusCode);
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Find("dav/notes.txt"))).StatusCode);
        Assert.Null(Entry("notes.txt"));
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.SendAsync(Req("PUT", "dav/notes.txt", new StringContent("typed"), ("If-Match", HeldETag)))).StatusCode);
        Assert.Equal("typed", await _owner.GetStringAsync("api/file?path=notes.txt"));
        Assert.Null(Entry("New Text Document.txt"));

        // renamed by letter case only: it simply follows
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/case.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/case.txt", null, ("Destination", "/dav/Case.txt"), ("Overwrite", "F")))).StatusCode);
        Assert.Equal((HttpStatusCode)207, (await made.Http.SendAsync(Find("dav/Case.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Find("dav/case.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/Case.txt")).StatusCode);

        // deleted before anything was written into it: gone, and never stored
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/gone.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/gone.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Find("dav/gone.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.DeleteAsync("dav/gone.txt")).StatusCode);
        Assert.Null(Entry("gone.txt"));

        // a held file in a new folder moves with the folder, and goes with it
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MKCOL", "dav/New%20folder"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/New%20folder/a.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.SendAsync(Req("MOVE", "dav/New%20folder", null, ("Destination", "/dav/Trip")))).StatusCode);
        Assert.Contains("<D:href>/dav/Trip/a.txt</D:href>", await BodyAsync(await made.Http.SendAsync(Find("dav/Trip", "1"))));
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.DeleteAsync("dav/Trip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Find("dav/Trip/a.txt"))).StatusCode);

        // renamed onto a stored file it is a real change: that file becomes empty and its old version is kept
        var taken = Entry("taken.txt")!;
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/blank.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await made.Http.SendAsync(Req("MOVE", "dav/blank.txt", null, ("Destination", "/dav/taken.txt"), ("Overwrite", "F")))).StatusCode);
        Assert.Equal(taken, Entry("taken.txt"));
        Assert.Equal(HttpStatusCode.NoContent, (await made.Http.SendAsync(Req("MOVE", "dav/blank.txt", null, ("Destination", "/dav/taken.txt")))).StatusCode);
        Assert.Equal(0, Entry("taken.txt")!.Size);
        Assert.Contains(_server.Store.ListHistory("taken.txt"), h => h.Hash8 == taken.Hash![..8]);
        Assert.Equal(HttpStatusCode.NotFound, (await made.Http.SendAsync(Find("dav/blank.txt"))).StatusCode);
        Assert.Null(Entry("blank.txt"));
    }

    [Fact]
    public async Task OnlyALinkThatMayChangeAPlaceMakesAnEmptyFileThere()
    {
        var view = await LinkAsync("Viewer", "view");
        var photos = await LinkAsync("Photos only", "edit", ["Photos"]);
        var edit = await LinkAsync("Editor", "edit");

        Assert.Equal(HttpStatusCode.Forbidden, (await view.Http.PutAsync("dav/v.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await photos.Http.PutAsync("dav/top.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await edit.Http.SendAsync(Find("dav/v.txt"))).StatusCode); // nothing was held for them
        Assert.Equal(HttpStatusCode.NotFound, (await edit.Http.SendAsync(Find("dav/top.txt"))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await photos.Http.PutAsync("dav/Photos/new.jpg", Empty())).StatusCode);
        Assert.Contains("<D:href>/dav/Photos/</D:href>", await BodyAsync(await photos.Http.SendAsync(Find("dav/", "1"))));
        Assert.Contains("<D:href>/dav/Photos/new.jpg</D:href>", await BodyAsync(await photos.Http.SendAsync(Find("dav/Photos", "1"))));
        Assert.Equal(HttpStatusCode.Forbidden, (await view.Http.DeleteAsync("dav/Photos/new.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await view.Http.SendAsync(Req("MOVE", "dav/Photos/new.jpg", null, ("Destination", "/dav/Photos/x.jpg")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await photos.Http.SendAsync(Req("MOVE", "dav/Photos/new.jpg", null, ("Destination", "/dav/x.jpg")))).StatusCode);
        Assert.Equal((HttpStatusCode)207, (await photos.Http.SendAsync(Find("dav/Photos/new.jpg"))).StatusCode);
        Assert.Null(Entry("Photos/new.jpg"));
    }

    /// <summary>A server of its own whose held empty files run on <paramref name="clock"/> (or are <paramref name="emptyFiles"/>), with an edit link "Mac" on it.</summary>
    private async Task<Clocked> ClockedServerAsync(ManualClock clock, DavEmptyFiles? emptyFiles = null)
    {
        var held = emptyFiles ?? new DavEmptyFiles(clock);
        var server = await TestServer.StartAsync(configureBuilder: b => b.Services.AddSingleton(held));
        var owner = server.RawHttp(server.Token);
        var resp = await owner.PostAsJsonAsync("api/webdav/links", new { label = "Mac", access = "edit" }, PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new Clocked(server, owner, Dav("mac", doc.RootElement.GetProperty("password").GetString()!, server), doc.RootElement.GetProperty("id").GetString()!, held,
            server.Services.GetRequiredService<DavEmptyFileKeeper>());
    }

    private sealed record Clocked(TestServer Server, HttpClient Owner, HttpClient Dav, string LinkId, DavEmptyFiles Held, DavEmptyFileKeeper Keeper) : IAsyncDisposable
    {
        public ManifestEntry? Entry(string path) => Server.Store.Manifest.Get(path);

        public long Changes() => Server.Store.ReadManifest(null).Version;

        public async ValueTask DisposeAsync()
        {
            Dav.Dispose();
            Owner.Dispose();
            await Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnEmptyFileNothingIsWrittenIntoIsStoredOnceTheClientIsDoneWithIt()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var s = await ClockedServerAsync(clock);
        var computer = s.Server.MintKey("PC-1").Key;
        var heard = new System.Collections.Concurrent.ConcurrentQueue<(string Device, string Path)>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(s.Server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(computer)).Build();
        hub.On<string, string>(SyncHub.ChangedMethod, (device, path) => heard.Enqueue((device, path)));
        await hub.StartAsync();
        await hub.InvokeAsync(nameof(SyncHub.Synced), 0L);
        var before = s.Changes();
        var made = clock.Now;

        // Finder copying an empty file: an empty PUT makes it, a LOCK, the content (empty too), an UNLOCK
        Assert.Equal(HttpStatusCode.Created, (await s.Dav.PutAsync("dav/empty.txt", Empty())).StatusCode);
        var locked = await s.Dav.SendAsync(Req("LOCK", "dav/empty.txt", new StringContent(LockBody)));
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Dav.PutAsync("dav/empty.txt", Empty())).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(30));
        await s.Keeper.StoreDueAsync();
        Assert.Null(s.Entry("empty.txt")); // locked: its content may still be on its way
        Assert.Equal(HttpStatusCode.NoContent, (await s.Dav.SendAsync(Req("UNLOCK", "dav/empty.txt", null, ("Lock-Token", locked.Headers.GetValues("Lock-Token").Single())))).StatusCode);
        clock.Advance(DavEmptyFiles.QuietTime - TimeSpan.FromSeconds(1));
        await s.Keeper.StoreDueAsync();
        Assert.Null(s.Entry("empty.txt"));
        Assert.Equal((HttpStatusCode)207, (await s.Dav.SendAsync(Find("dav/empty.txt"))).StatusCode); // there all along for the client

        clock.Advance(TimeSpan.FromSeconds(1)); // nothing more came: it was meant to be empty
        await s.Keeper.StoreDueAsync();
        var stored = s.Entry("empty.txt")!;
        Assert.Equal((0L, before + 1, made.ToUnixTimeMilliseconds()), (stored.Size, stored.Version, stored.ModifiedMs)); // one version, made when the client made it
        Assert.Empty(s.Server.Store.ListHistory("empty.txt"));
        Assert.Contains("<D:getetag>\"" + stored.Hash + "\"</D:getetag>", await BodyAsync(await s.Dav.SendAsync(Find("dav/empty.txt"))));
        Assert.Equal(string.Empty, await s.Owner.GetStringAsync("api/file?path=empty.txt"));
        await WaitUntilAsync(() => heard.Contains(("Mac (WebDAV link)", "empty.txt"))); // computers hear it, in the link's name

        // a client that only PUTs, without a lock: stored once nothing more came for the quiet time
        Assert.Equal(HttpStatusCode.Created, (await s.Dav.PutAsync("dav/plain.txt", Empty())).StatusCode);
        clock.Advance(DavEmptyFiles.QuietTime);
        await s.Keeper.StoreDueAsync();
        Assert.Equal((0L, before + 2), (s.Entry("plain.txt")!.Size, s.Changes()));
        Assert.Equal(0, s.Held.Count);
    }

    [Fact]
    public async Task ContentThatTakesLongToArriveKeepsTheEmptyFileFromBeingStoredFirst()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var s = await ClockedServerAsync(clock);
        var before = s.Changes();
        Assert.Equal(HttpStatusCode.Created, (await s.Dav.PutAsync("dav/slow.bin", Empty())).StatusCode);

        var gate = new TaskCompletionSource();
        var put = s.Dav.PutAsync("dav/slow.bin", new SlowContent(new byte[64 * 1024], gate.Task, new byte[64 * 1024]));
        await WaitUntilAsync(() => Directory.EnumerateFiles(s.Server.Paths.Tmp).Any()); // the nest is receiving it
        clock.Advance(DavEmptyFiles.LockedTime + DavEmptyFiles.QuietTime);
        await s.Keeper.StoreDueAsync();
        Assert.Null(s.Entry("slow.bin")); // never stored empty while its content comes in
        gate.SetResult();

        Assert.Equal(HttpStatusCode.NoContent, (await put).StatusCode);
        var stored = s.Entry("slow.bin")!;
        Assert.Equal((128L * 1024, before + 1), (stored.Size, stored.Version));
        Assert.Empty(s.Server.Store.ListHistory("slow.bin"));
        Assert.Equal(0, s.Held.Count);
    }

    [Fact]
    public async Task AHeldEmptyFileIsNeverStoredOnceItsLinkIsRemoved()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var s = await ClockedServerAsync(clock);
        var before = s.Changes();

        // removed in the apps or on the nest's website: what it made goes at once
        Assert.Equal(HttpStatusCode.Created, (await s.Dav.PutAsync("dav/a.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"api/webdav/links/{s.LinkId}")).StatusCode);
        Assert.Equal(0, s.Held.Count);

        // removed on the command line (another process): the keeper finds it removed and stores nothing
        var resp = await s.Owner.PostAsJsonAsync("api/webdav/links", new { label = "PC", access = "edit" }, PairnetsJson.Options);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        using var pc = Dav("pc", doc.RootElement.GetProperty("password").GetString()!, s.Server);
        Assert.Equal(HttpStatusCode.Created, (await pc.PutAsync("dav/b.txt", Empty())).StatusCode);
        Assert.Equal(0, await CliCommands.RunAsync(["devices", "remove", doc.RootElement.GetProperty("id").GetString()!, "--data-dir", s.Server.DataDir], new StringWriter(), new StringWriter()));
        clock.Advance(DavEmptyFiles.QuietTime);
        await s.Keeper.StoreDueAsync();

        Assert.Null(s.Entry("a.txt"));
        Assert.Null(s.Entry("b.txt"));
        Assert.Equal(before, s.Changes());
        Assert.Equal(0, s.Held.Count);
        Assert.Contains(s.Server.Logs.Lines, l => l.Contains("Dropped the empty file b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoppingNestStoresTheEmptyFilesNobodyHoldsALockOn()
    {
        var made = await LinkAsync();
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/plain.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await made.Http.PutAsync("dav/locked.txt", Empty())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await made.Http.SendAsync(Req("LOCK", "dav/locked.txt", new StringContent(LockBody)))).StatusCode);

        await _server.RestartAsync();
        Assert.Equal(0, Entry("plain.txt")!.Size);
        Assert.Null(Entry("locked.txt")); // its content was on its way: the client puts it again after the restart
        using var again = Dav(made.Username, made.Password);
        Assert.Equal((HttpStatusCode)207, (await again.SendAsync(Find("dav/plain.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await again.SendAsync(Find("dav/locked.txt"))).StatusCode);
    }

    [Fact]
    public async Task WhenTooManyEmptyFilesAreHeldAnEmptyPutStoresItsFileAtOnce()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var full = new DavEmptyFiles(clock);
        var someone = new DeviceIdentity("dev_other", "Other (WebDAV link)", DeviceAuthKind.WebDavKey);
        for (var i = 0; i < DavEmptyFiles.MaxHeld; i++)
            Assert.True(full.TryAdd($"held/{i}.txt", someone));
        await using var s = await ClockedServerAsync(clock, full);

        var created = await s.Dav.PutAsync("dav/now.txt", Empty());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var stored = s.Entry("now.txt")!;
        Assert.Equal(0, stored.Size);
        Assert.Equal("\"" + stored.Hash + "\"", created.Headers.ETag?.Tag);
        Assert.Null(full.MadeAt("now.txt"));
    }

    /// <summary>A body that sends its first part, waits for <paramref name="gate"/>, then sends the rest (a slow upload).</summary>
    private sealed class SlowContent(byte[] first, Task gate, byte[] rest) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(first);
            await stream.FlushAsync();
            await gate;
            await stream.WriteAsync(rest);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = first.Length + rest.Length;
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never came true.");
            await Task.Delay(20);
        }
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
