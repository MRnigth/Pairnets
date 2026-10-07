using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Core;
using Pairnets.Core.Hashing;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>The server side of uploads in pieces (POST/PUT/GET/DELETE /api/upload, commit).</summary>
public class UploadInPiecesApiTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _http = _server.RawHttp(_server.Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private static byte[] Data(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private async Task<HttpResponseMessage> Start(string path, string @base, long size) =>
        await _http.PostAsync($"api/upload?path={Uri.EscapeDataString(path)}&base={@base}&size={size}&mtime=1700000000000", null);

    private async Task<string> StartOk(string path, long size, string @base = "none")
    {
        var resp = await Start(path, @base, size);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var status = (await resp.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!;
        Assert.Equal(0, status.Received);
        return status.Id;
    }

    private async Task<HttpResponseMessage> Piece(string id, long offset, byte[] data) =>
        await _http.PutAsync($"api/upload/{id}?offset={offset}", new ByteArrayContent(data));

    private async Task<HttpResponseMessage> Commit(string id, string hash) =>
        await _http.PostAsync($"api/upload/{id}/commit?hash={hash}", null);

    private static async Task<string?> Code(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))?.Code;

    private string[] PartFiles() => Directory.GetFiles(_server.Paths.Tmp, "*.part");

    [Fact]
    public async Task PiecesAreJoinedAndCommittedLikeOnePut()
    {
        var data = Data(300);
        var id = await StartOk("dir/big.bin", data.Length);
        for (var offset = 0; offset < data.Length; offset += 100)
        {
            var resp = await Piece(id, offset, data[offset..(offset + 100)]);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(offset + 100, (await resp.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!.Received);
        }
        var status = await _http.GetFromJsonAsync<UploadStatus>($"api/upload/{id}", PairnetsJson.Options);
        Assert.Equal(300, status!.Received);

        var commit = await Commit(id, ContentHash.Of(data));
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var entry = (await commit.Content.ReadFromJsonAsync<ManifestEntry>(PairnetsJson.Options))!;
        Assert.Equal(ContentHash.Of(data), entry.Hash);
        Assert.Equal(300, entry.Size);
        Assert.Equal(1700000000000, entry.ModifiedMs);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "dir", "big.bin")));
        Assert.Empty(PartFiles());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"api/upload/{id}")).StatusCode);
    }

    [Fact]
    public async Task APieceMustStartWhereTheServerCopyEnds()
    {
        var data = Data(200);
        var id = await StartOk("a.bin", data.Length);
        Assert.Equal(HttpStatusCode.OK, (await Piece(id, 0, data[..100])).StatusCode);

        var gap = await Piece(id, 150, data[150..]);
        Assert.Equal(HttpStatusCode.Conflict, gap.StatusCode);
        Assert.Equal(ErrorCodes.UploadOffset, await Code(gap));
        var again = await Piece(id, 0, data[..100]);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Committing early is refused but keeps the upload, which can then be finished.
        var early = await Commit(id, ContentHash.Of(data));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Piece(id, 100, data[100..])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Commit(id, ContentHash.Of(data))).StatusCode);
    }

    [Fact]
    public async Task WrongHashIsRefusedAndNothingIsStored()
    {
        var data = Data(100);
        var id = await StartOk("a.bin", data.Length);
        await Piece(id, 0, data);

        var commit = await Commit(id, ContentHash.Of(Data(100, seed: 2)));
        Assert.Equal(HttpStatusCode.BadRequest, commit.StatusCode);
        Assert.Equal(ErrorCodes.UploadMismatch, await Code(commit));
        Assert.Empty(_server.Store.ReadManifest(null).Entries);
        Assert.Empty(PartFiles());
        Assert.Equal(HttpStatusCode.NotFound, (await Commit(id, ContentHash.Of(data))).StatusCode);
    }

    [Fact]
    public async Task StartChecksNameBaseAndCaseLikeOnePut()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsync("api/file?path=Report.txt&base=none", new StringContent("x"))).StatusCode);

        var conflict = await Start("Report.txt", "none", 10);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await Code(conflict));

        var collision = await Start("report.TXT", "none", 10);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        Assert.Equal(ErrorCodes.CaseCollision, await Code(collision));

        var invalid = await Start("CON.txt", "none", 10);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(ErrorCodes.InvalidName, await Code(invalid));

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync("api/upload?path=b.txt&base=none&size=-1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync("api/upload?path=b.txt&base=none", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync("api/upload?path=b.txt&base=nonsense&size=1", null)).StatusCode);
        Assert.Equal(0, _server.Uploads.Count);
    }

    [Fact]
    public async Task ACommitAfterAnotherChangeIsAConflict()
    {
        var data = Data(50);
        var id = await StartOk("a.bin", data.Length);
        await Piece(id, 0, data);
        // Meanwhile the other PC stored the same name.
        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsync("api/file?path=a.bin&base=none", new StringContent("other"))).StatusCode);

        var commit = await Commit(id, ContentHash.Of(data));
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await Code(commit));
        Assert.Equal("other", await File.ReadAllTextAsync(Path.Combine(_server.Paths.Files, "a.bin")));
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task MoreThanTheDeclaredSizeDropsTheUpload()
    {
        var id = await StartOk("a.bin", 10);
        var resp = await Piece(id, 0, Data(20));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"api/upload/{id}")).StatusCode);
        Assert.Empty(PartFiles());
    }

    [Fact]
    public async Task APieceOverTheLimitIsRefusedBeforeItsBodyIsRead()
    {
        var id = await StartOk("a.bin", UploadSessions.MaxChunkBytes + 1);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"api/upload/{id}?offset=0") { Content = new ByteArrayContent([1, 2, 3]) };
        req.Content.Headers.ContentLength = UploadSessions.MaxChunkBytes + 1;
        req.Headers.ExpectContinue = true;
        HttpResponseMessage? resp = null;
        try
        {
            resp = await _http.SendAsync(req);
        }
        catch (HttpRequestException)
        {
            // The client may also fail to send the (fake) body after the server answered.
        }
        if (resp is not null)
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
            Assert.Equal(ErrorCodes.TooLarge, await Code(resp));
        }
        Assert.Equal(0, (await _http.GetFromJsonAsync<UploadStatus>($"api/upload/{id}", PairnetsJson.Options))!.Received);
    }

    [Fact]
    public async Task AbortDropsTheUploadAndItsPartFile()
    {
        var id = await StartOk("a.bin", 100);
        await Piece(id, 0, Data(40));
        Assert.Single(PartFiles());

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"api/upload/{id}")).StatusCode);
        Assert.Empty(PartFiles());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"api/upload/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Piece(id, 40, Data(60))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"api/upload/{id}")).StatusCode);
    }

    [Fact]
    public async Task UploadsNeedTheToken()
    {
        var id = await StartOk("a.bin", 10);
        using var anon = _server.RawHttp();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("api/upload?path=b.bin&base=none&size=1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync($"api/upload/{id}?offset=0", new ByteArrayContent(Data(10)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"api/upload/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync($"api/upload/{id}")).StatusCode);
        Assert.Equal(0, (await _http.GetFromJsonAsync<UploadStatus>($"api/upload/{id}", PairnetsJson.Options))!.Received);
    }

    [Fact]
    public async Task ResponsesAreNeverCached()
    {
        await _http.PutAsync("api/file?path=a.txt&base=none", new StringContent("x"));
        foreach (var url in new[] { "api/file?path=a.txt", "api/manifest", "api/info", "api/file?path=missing.txt" })
        {
            var resp = await _http.GetAsync(url);
            Assert.True(resp.Headers.CacheControl?.NoStore, url);
            Assert.True(resp.Headers.CacheControl?.NoTransform, url);
        }
    }
}

/// <summary>Upload sessions that need control over time.</summary>
public class UploadSessionStoreTests
{
    [Fact]
    public async Task IdleSessionsExpireAndTheirPartFilesGo()
    {
        using var dir = new TempDir("sessions");
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new SyncStore(new ServerPaths(dir.Path), NullLogger<SyncStore>.Instance, clock);
        store.Initialize();
        var sessions = new UploadSessions(store, NullLogger<UploadSessions>.Instance, clock);

        var idle = await sessions.StartAsync("idle.bin", "none", 10, null, default);
        clock.Advance(TimeSpan.FromMinutes(50));
        var busy = await sessions.StartAsync("busy.bin", "none", 10, null, default);
        Assert.Equal(UploadSessions.ChunkStatus.Ok, (await sessions.AppendAsync(busy.Id!, 0, new MemoryStream(new byte[5]), default)).Status);
        Assert.Equal(2, Directory.GetFiles(store.Paths.Tmp).Length);

        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(1, sessions.ExpireIdle());
        Assert.Null(sessions.Received(idle.Id!));
        Assert.Equal(5, sessions.Received(busy.Id!));
        Assert.Single(Directory.GetFiles(store.Paths.Tmp));
    }

    [Fact]
    public async Task TooManyOpenUploadsAreRefused()
    {
        using var dir = new TempDir("sessions");
        using var store = new SyncStore(new ServerPaths(dir.Path), NullLogger<SyncStore>.Instance);
        store.Initialize();
        var sessions = new UploadSessions(store, NullLogger<UploadSessions>.Instance);
        for (var i = 0; i < UploadSessions.MaxSessions; i++)
            Assert.Equal(UploadSessions.StartStatus.Started, (await sessions.StartAsync($"f{i}.bin", "none", 1, null, default)).Status);
        Assert.Equal(UploadSessions.StartStatus.TooMany, (await sessions.StartAsync("one-more.bin", "none", 1, null, default)).Status);
    }

    [Fact]
    public async Task ABrokenPieceKeepsWhatArrived()
    {
        using var dir = new TempDir("sessions");
        using var store = new SyncStore(new ServerPaths(dir.Path), NullLogger<SyncStore>.Instance);
        store.Initialize();
        var sessions = new UploadSessions(store, NullLogger<UploadSessions>.Instance);
        var data = Encoding.UTF8.GetBytes("0123456789abcdefghij");
        var id = (await sessions.StartAsync("a.txt", "none", data.Length, null, default)).Id!;

        await Assert.ThrowsAsync<IOException>(() => sessions.AppendAsync(id, 0, new BreaksAfter(data[..12], 7), default));
        Assert.Equal(7, sessions.Received(id));
        Assert.Equal(UploadSessions.ChunkStatus.Ok, (await sessions.AppendAsync(id, 7, new MemoryStream(data[7..]), default)).Status);
        var done = await sessions.CommitAsync(id, ContentHash.Of(data), default);
        Assert.Equal(UploadSessions.CommitStatus.Done, done.Status);
        Assert.Equal(ChangeStatus.Ok, done.Change!.Status);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(store.Paths.Files, "a.txt")));
    }

    /// <summary>A request body whose connection breaks after <paramref name="limit"/> bytes.</summary>
    private sealed class BreaksAfter(byte[] data, int limit) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit)
                throw new IOException("connection reset");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - Position)], cancellationToken);
        }
    }
}
