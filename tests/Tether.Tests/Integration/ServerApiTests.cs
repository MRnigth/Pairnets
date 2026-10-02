using System.Net;
using System.Net.Http.Json;
using System.Text;
using Tether.Core;
using Tether.Core.Api;
using Tether.Core.Hashing;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Integration;

public class ServerApiTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static string H(string s) => ContentHash.Of(Encoding.UTF8.GetBytes(s));

    private async Task<HttpResponseMessage> Put(string path, string @base, string content, string? token = null)
    {
        using var http = _server.RawHttp(token ?? _server.Token);
        return await http.PutAsync($"api/file?path={Uri.EscapeDataString(path)}&base={@base}&mtime=1700000000000", new StringContent(content));
    }

    private static async Task<string?> Code(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorBody>(TetherJson.Options))?.Code;

    [Fact]
    public async Task HealthNeedsNoAuth()
    {
        using var http = _server.RawHttp();
        Assert.Equal("ok", await http.GetStringAsync("api/health"));
    }

    [Fact]
    public async Task WrongOrMissingTokenIsRejectedEverywhere()
    {
        using var none = _server.RawHttp();
        using var wrong = _server.RawHttp("wrong-token-wrong-token");
        foreach (var url in new[] { "api/manifest", "api/info", "api/file?path=a", "api/history?path=a" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await none.GetAsync(url)).StatusCode);
            var resp = await wrong.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal(ErrorCodes.Unauthorized, await Code(resp));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put("a.txt", "none", "x", "wrong-token-wrong-token")).StatusCode);
        // access_token in the query is only accepted for the SignalR hub.
        Assert.Equal(HttpStatusCode.Unauthorized, (await none.GetAsync($"api/manifest?access_token={_server.Token}")).StatusCode);
        Assert.Empty(_server.Store.ReadManifest(null).Entries);
    }

    [Fact]
    public async Task AllTokenFormsWork()
    {
        using var http = _server.RawHttp();
        var bearer = new HttpRequestMessage(HttpMethod.Get, "api/info");
        bearer.Headers.Authorization = new("Bearer", _server.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(bearer)).StatusCode);
        using var header = _server.RawHttp(_server.Token);
        Assert.Equal(HttpStatusCode.OK, (await header.GetAsync("api/info")).StatusCode);
        var negotiate = await http.PostAsync($"hub/negotiate?negotiateVersion=1&access_token={_server.Token}", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
    }

    [Fact]
    public async Task TokenIsNeverLogged()
    {
        using var http = _server.RawHttp(_server.Token);
        await http.GetAsync("api/manifest");
        await Put("x.txt", "none", "x");
        using var anon = _server.RawHttp();
        await anon.PostAsync($"hub/negotiate?negotiateVersion=1&access_token={_server.Token}", null);
        await anon.GetAsync($"api/manifest?access_token={_server.Token}");
        Assert.NotEmpty(_server.Logs.Lines);
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains(_server.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PutCreatesUpdatesAndKeepsHistory()
    {
        var r1 = await Put("docs/a.txt", "none", "v1");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var e1 = (await r1.Content.ReadFromJsonAsync<ManifestEntry>(TetherJson.Options))!;
        Assert.Equal(H("v1"), e1.Hash);
        Assert.Equal(1700000000000, e1.ModifiedMs);

        var stale = await Put("docs/a.txt", "none", "v2");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await Code(stale));

        var r2 = await Put("docs/a.txt", H("v1"), "v2");
        var e2 = (await r2.Content.ReadFromJsonAsync<ManifestEntry>(TetherJson.Options))!;
        Assert.True(e2.Version > e1.Version);

        // Same content again: 200 without a new version.
        var same = await Put("docs/a.txt", H("v2"), "v2");
        Assert.Equal(e2.Version, (await same.Content.ReadFromJsonAsync<ManifestEntry>(TetherJson.Options))!.Version);

        var history = _server.Store.ListHistory("docs/a.txt");
        Assert.Single(history);
        Assert.Equal(H("v1")[..8], history[0].Hash8);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(_server.Paths.Files, "docs", "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(_server.Paths.Tmp));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/x.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("CON")]
    [InlineData("dir/nul.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("what?.txt")]
    [InlineData("a//b")]
    [InlineData(".tether-tmp/x")]
    public async Task InvalidNamesAreRejected(string path)
    {
        var resp = await Put(path, "none", "x");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ErrorCodes.InvalidName, await Code(resp));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_server.Paths.Files));
    }

    [Fact]
    public async Task TooLongPathIsRejected()
    {
        var resp = await Put(new string('a', 1025), "none", "x");
        Assert.Equal(ErrorCodes.InvalidName, await Code(resp));
    }

    [Fact]
    public async Task TraversalNeverReadsOutsideFiles()
    {
        using var http = _server.RawHttp(_server.Token);
        foreach (var path in new[] { "../manifest.db", "..%2Fmanifest.db", "/etc/passwd", "files/../manifest.db" })
        {
            var resp = await http.GetAsync("api/file?path=" + path);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task CaseCollisionsAreRejected()
    {
        Assert.Equal(HttpStatusCode.OK, (await Put("Docs/Report.txt", "none", "x")).StatusCode);
        foreach (var path in new[] { "docs/report.txt", "DOCS/other.txt", "Docs/REPORT.TXT", "Docs/Report.txt/child", "Docs" })
        {
            var resp = await Put(path, "none", "y");
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            Assert.Equal(ErrorCodes.CaseCollision, await Code(resp));
        }
        // After deleting, the other case is allowed.
        using var http = _server.RawHttp(_server.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync($"api/file?path=Docs/Report.txt&base={H("x")}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put("docs/report.txt", "none", "y")).StatusCode);
    }

    [Fact]
    public async Task DeleteChecksBaseAndIsIdempotent()
    {
        await Put("a.txt", "none", "x");
        using var http = _server.RawHttp(_server.Token);
        var wrong = await http.DeleteAsync($"api/file?path=a.txt&base={H("other")}");
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);

        var ok = await http.DeleteAsync($"api/file?path=a.txt&base={H("x")}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True((await ok.Content.ReadFromJsonAsync<ManifestEntry>(TetherJson.Options))!.Deleted);
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync($"api/file?path=a.txt&base={H("x")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"api/file?path=never.txt&base={H("x")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/file?path=a.txt")).StatusCode);
        Assert.Single(_server.Store.ListHistory("a.txt"));
        Assert.False(File.Exists(Path.Combine(_server.Paths.Files, "a.txt")));
    }

    [Fact]
    public async Task ManifestSinceReturnsDeltaAndTombstones()
    {
        using var api = _server.Client();
        await Put("a.txt", "none", "a");
        var first = await api.GetManifestAsync(null, default);
        await Put("b.txt", "none", "b");
        using var http = _server.RawHttp(_server.Token);
        await http.DeleteAsync($"api/file?path=a.txt&base={H("a")}");

        var delta = await api.GetManifestAsync(first.Version, default);
        Assert.Equal(["b.txt", "a.txt"], delta.Entries.Select(e => e.Path));
        Assert.True(delta.Entries[1].Deleted);
        Assert.Equal(first.ServerId, delta.ServerId);
        Assert.Equal(3, delta.Version);
        var all = await api.GetManifestAsync(null, default);
        Assert.Equal(2, all.Entries.Count);
    }

    [Fact]
    public async Task DownloadSupportsRangeAndVerification()
    {
        await Put("r.txt", "none", "0123456789");
        using var http = _server.RawHttp(_server.Token);
        var req = new HttpRequestMessage(HttpMethod.Get, "api/file?path=r.txt");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal("2345", await resp.Content.ReadAsStringAsync());

        using var api = _server.Client();
        var ms = new MemoryStream();
        var dl = await api.DownloadAsync("r.txt", ms, null, default);
        Assert.True(dl.Found);
        Assert.Equal(H("0123456789"), dl.Hash);
        Assert.Equal(1700000000000, dl.ModifiedMs);
    }

    [Fact]
    public async Task HistoryRestoreCreatesNewVersion()
    {
        await Put("h.txt", "none", "old");
        await Put("h.txt", H("old"), "new");
        using var api = _server.Client();
        var history = await api.GetHistoryAsync("h.txt", default);
        var version = Assert.Single(history);
        var restored = await api.RestoreAsync("h.txt", version.Id, default);
        Assert.Equal(ApiOutcome.Ok, restored.Outcome);
        Assert.Equal(H("old"), restored.Entry!.Hash);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(_server.Paths.Files, "h.txt")));
        Assert.Equal(2, (await api.GetHistoryAsync("h.txt", default)).Count); // "new" is kept too
        Assert.Equal(ApiOutcome.NotFound, (await api.RestoreAsync("h.txt", "20200101T000000000Z-deadbeef", default)).Outcome);
    }

    [Fact]
    public async Task ApiClientMapsOutcomes()
    {
        using var api = _server.Client();
        var (ok, sent, bytes) = await api.UploadAsync("c.txt", "none", 1, new MemoryStream("hi"u8.ToArray()), null, default);
        Assert.Equal(ApiOutcome.Ok, ok.Outcome);
        Assert.Equal(H("hi"), sent);
        Assert.Equal(2, bytes);
        Assert.Equal(ApiOutcome.Conflict, (await api.UploadAsync("c.txt", "none", 1, new MemoryStream("x"u8.ToArray()), null, default)).Result.Outcome);
        Assert.Equal(ApiOutcome.CaseCollision, (await api.UploadAsync("C.txt", "none", 1, new MemoryStream("x"u8.ToArray()), null, default)).Result.Outcome);
        Assert.Equal(ApiOutcome.InvalidName, (await api.UploadAsync("aux.txt", "none", 1, new MemoryStream("x"u8.ToArray()), null, default)).Result.Outcome);

        using var bad = _server.Client(token: "definitely-the-wrong-token");
        await Assert.ThrowsAsync<TetherAuthException>(() => bad.GetManifestAsync(null, default));

        var test = await TetherApiClient.TestConnectionAsync(_server.Url.ToString(), _server.Token, "t");
        Assert.Equal(ConnectionTestStatus.Ok, test.Status);
        Assert.Equal(ConnectionTestStatus.BadToken, (await TetherApiClient.TestConnectionAsync(_server.Url.ToString(), "nope-nope-nope-nope", "t")).Status);
        Assert.Equal(ConnectionTestStatus.InvalidUrl, (await TetherApiClient.TestConnectionAsync("ftp://x", "t", "t")).Status);
        Assert.Equal(ConnectionTestStatus.Unreachable, (await TetherApiClient.TestConnectionAsync("http://127.0.0.1:1/", "token-token-token", "t")).Status);
    }

    /// <summary>Sends part of a body, then goes silent without closing: a connection that died without a reset.</summary>
    private sealed class StallingContent(int bytesBeforeStall, CancellationToken hold) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(new byte[bytesBeforeStall], hold);
            await stream.FlushAsync(hold);
            await Task.Delay(Timeout.Infinite, hold);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task StalledUploadIsDiscardedAfterTheStallTimeout()
    {
        using var hold = new CancellationTokenSource();
        using var http = _server.RawHttp(_server.Token);
        var put = http.PutAsync("api/file?path=stalled.bin&base=none", new StallingContent(512 * 1024, hold.Token), hold.Token);

        // The partial upload appears in tmp/ and is deleted once the 2 s test stall timeout fires.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Directory.EnumerateFiles(_server.Paths.Tmp).Any() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.NotEmpty(Directory.EnumerateFiles(_server.Paths.Tmp));
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (Directory.EnumerateFiles(_server.Paths.Tmp).Any() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Empty(Directory.EnumerateFiles(_server.Paths.Tmp));
        Assert.Null(_server.Store.Manifest.Get("stalled.bin"));
        Assert.False(File.Exists(Path.Combine(_server.Paths.Files, "stalled.bin")));

        await hold.CancelAsync();
        try
        {
            await put;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
        }
    }
}
