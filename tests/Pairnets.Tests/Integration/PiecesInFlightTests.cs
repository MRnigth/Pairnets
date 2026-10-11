using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Two pieces of one upload on the way at once: the server keeps the second one waiting until the first has arrived
/// ("turn=wait"), and the app sends the second as soon as the first one's bytes are out. Through Cloudflare, which
/// takes in a whole piece before it passes it on, this keeps both lines busy instead of taking turns.
/// </summary>
public class PiecesInFlightTests : IAsyncLifetime
{
    private const long Piece = 64 * 1024;

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

    private async Task<string> StartOk(long size)
    {
        var resp = await _http.PostAsync($"api/upload?path=big.bin&base=none&size={size}&mtime=1700000000000", null);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!.Id;
    }

    private async Task<UploadStatus> Status(string id) =>
        (await _http.GetFromJsonAsync<UploadStatus>($"api/upload/{id}", PairnetsJson.Options))!;

    private static async Task<long> Received(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!.Received;
    }

    /// <summary>A piece whose first half is sent at once and the rest only when told (or never: the request is then cut).</summary>
    private sealed class HeldBody(byte[] data) : HttpContent
    {
        public TaskCompletionSource FirstHalfSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True: send the rest. False: cut the request.</summary>
        public TaskCompletionSource<bool> Rest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var half = data.Length / 2;
            await stream.WriteAsync(data.AsMemory(0, half));
            await stream.FlushAsync();
            FirstHalfSent.TrySetResult();
            if (!await Rest.Task)
                throw new IOException("Cut on purpose.");
            await stream.WriteAsync(data.AsMemory(half));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = data.Length;
            return true;
        }
    }

    /// <summary>Starts the first piece (0–100) and waits until the server has its first half.</summary>
    private async Task<(Task<HttpResponseMessage> First, HeldBody Body)> FirstPieceHalfwayAsync(string id, byte[] data)
    {
        var body = new HeldBody(data[..100]);
        var first = _http.PutAsync($"api/upload/{id}?offset=0", body);
        await body.FirstHalfSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await Status(id)).Received < 50)
        {
            Assert.True(DateTime.UtcNow < deadline, "the server never took in the first half of the first piece");
            await Task.Delay(20);
        }
        return (first, body);
    }

    [Fact]
    public async Task TheServerSaysItTakesTwoPiecesAtOnce()
    {
        var info = await _http.GetFromJsonAsync<ServerInfo>("api/info", PairnetsJson.Options);
        Assert.Equal(2, info!.PiecesInFlight);
    }

    [Fact]
    public async Task APieceSentEarlyWaitsForTheOneBeforeIt()
    {
        var data = Data(200);
        var id = await StartOk(data.Length);
        var (first, body) = await FirstPieceHalfwayAsync(id, data);

        var second = _http.PutAsync($"api/upload/{id}?offset=100&turn=wait", new ByteArrayContent(data[100..]));
        await Task.Delay(300);
        Assert.False(second.IsCompleted, "the second piece should wait for the first, not be refused as busy");

        body.Rest.SetResult(true);
        Assert.Equal(100, await Received(await first));
        Assert.Equal(200, await Received(await second));
        var commit = await _http.PostAsync($"api/upload/{id}/commit?hash={ContentHash.Of(data)}", null);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "big.bin")));
    }

    [Fact]
    public async Task APieceThatArrivesBeforeTheOneBeforeItWaitsForIt()
    {
        var data = Data(200);
        var id = await StartOk(data.Length);

        var second = _http.PutAsync($"api/upload/{id}?offset=100&turn=wait", new ByteArrayContent(data[100..]));
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        Assert.Equal(100, await Received(await _http.PutAsync($"api/upload/{id}?offset=0", new ByteArrayContent(data[..100]))));
        Assert.Equal(200, await Received(await second));
    }

    [Fact]
    public async Task WhenThePieceBeforeIsCutTheWaitingOneIsToldAtOnceWhereToGoOn()
    {
        var data = Data(200);
        var id = await StartOk(data.Length);
        var (first, body) = await FirstPieceHalfwayAsync(id, data);
        var second = _http.PutAsync($"api/upload/{id}?offset=100&turn=wait", new ByteArrayContent(data[100..]));
        await Task.Delay(200);

        var clock = Stopwatch.StartNew();
        body.Rest.SetResult(false);
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        var refused = await second;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed} (it should not wait the full minute)");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ErrorCodes.UploadOffset, (await refused.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))!.Code);
        Assert.Equal(50, (await Status(id)).Received);
    }

    [Fact]
    public async Task WithoutTurnWaitAPieceWhileAnotherArrivesIsStillBusy()
    {
        var data = Data(200);
        var id = await StartOk(data.Length);
        var (first, body) = await FirstPieceHalfwayAsync(id, data);

        var second = await _http.PutAsync($"api/upload/{id}?offset=100", new ByteArrayContent(data[100..]));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCodes.UploadBusy, (await second.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))!.Code);
        body.Rest.SetResult(true);
        Assert.Equal(100, await Received(await first));
    }

    // ------------------------------------------------------------------ the app

    /// <summary>Counts how many pieces are on the way at once, and answers each a little late (like a far-away server).</summary>
    private sealed class Watcher(TimeSpan delay) : DelegatingHandler(new SocketsHttpHandler())
    {
        private int _now;
        public int MostAtOnce;
        public readonly List<string> PieceUris = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var piece = request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.StartsWith("/api/upload/", StringComparison.Ordinal);
            if (!piece)
                return await base.SendAsync(request, cancellationToken);
            lock (PieceUris)
            {
                PieceUris.Add(request.RequestUri!.Query);
                MostAtOnce = Math.Max(MostAtOnce, ++_now);
            }
            try
            {
                var resp = await base.SendAsync(request, cancellationToken);
                await Task.Delay(delay, cancellationToken);
                return resp;
            }
            finally
            {
                lock (PieceUris)
                    _now--;
            }
        }
    }

    private PairnetsApiClient Client(HttpMessageHandler handler, Uri? url = null) =>
        new(url ?? _server.Url, _server.Token, "pc", handler, stallTimeout: TimeSpan.FromSeconds(20), pieceSize: Piece, minPieceSize: Piece);

    [Fact]
    public async Task TheAppLearnsItAndSendsTwoPiecesAtOnce()
    {
        var watcher = new Watcher(TimeSpan.FromMilliseconds(150));
        using var api = Client(watcher);
        await api.GetInfoAsync(default);
        Assert.Equal(2, api.PiecesInFlight);
        var data = Data((int)(Piece * 6 + 77));
        long lastProgress = 0;

        var (result, sentHash, sentBytes) = await api.UploadAsync("big.bin", ContentHash.NoneBase, 1700000000000, new MemoryStream(data), p => lastProgress = p, default);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        Assert.Equal(data.Length, sentBytes);
        Assert.Equal(data.Length, lastProgress);
        Assert.Equal(2, watcher.MostAtOnce);
        Assert.Equal(7, watcher.PieceUris.Count);
        Assert.Contains(watcher.PieceUris, q => q.Contains("turn=wait", StringComparison.Ordinal));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "big.bin")));
        Assert.Equal(0, _server.Uploads.Count);
    }

    [Fact]
    public async Task WithoutTheServersWordPiecesGoOneAtATime()
    {
        var watcher = new Watcher(TimeSpan.FromMilliseconds(50));
        using var api = Client(watcher);
        var data = Data((int)(Piece * 4));

        var (result, _, _) = await api.UploadAsync("big.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(1, watcher.MostAtOnce);
        Assert.DoesNotContain(watcher.PieceUris, q => q.Contains("turn=wait", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACutWithTwoPiecesOnTheWayStillEndsWithTheWholeFile()
    {
        await using var proxy = new ChaosProxy(_server.Port);
        using var api = Client(new Watcher(TimeSpan.FromMilliseconds(20)), proxy.Url);
        api.PiecesInFlight = 2;
        var data = Data((int)(Piece * 8));
        proxy.CutAfterUpstreamBytes = Piece * 5 / 2;
        using var heal = new CancellationTokenSource();
        var healer = Task.Run(async () =>
        {
            while (Volatile.Read(ref proxy.Cuts) == 0 && !heal.IsCancellationRequested)
                await Task.Delay(10);
            proxy.CutAfterUpstreamBytes = null;
        });

        var (result, sentHash, _) = await api.UploadAsync("big.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default);
        heal.Cancel();
        await healer;

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        Assert.True(proxy.Cuts > 0);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "big.bin")));
    }

    [Fact]
    public async Task PausingWithTwoPiecesOnTheWayKeepsTheUploadForLater()
    {
        var watcher = new Watcher(TimeSpan.FromMilliseconds(200));
        using var api = Client(watcher);
        api.PiecesInFlight = 2;
        var data = Data((int)(Piece * 10));
        var resume = new UploadResume();
        using var pause = new CancellationTokenSource();
        long seen = 0;

        var upload = api.UploadAsync("big.bin", ContentHash.NoneBase, 0, new MemoryStream(data), p =>
        {
            if (Interlocked.Exchange(ref seen, p) < Piece * 3 && p >= Piece * 3)
                pause.Cancel();
        }, pause.Token, resume);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);

        Assert.NotNull(resume.Id);
        var (result, sentHash, _) = await api.UploadAsync("big.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default, resume);
        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "big.bin")));
    }
}
