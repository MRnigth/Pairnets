using System.Net;
using System.Text;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>The client side of uploads in pieces: when it is used, falling back, resuming, cancelling.</summary>
public class UploadInPiecesClientTests : IAsyncLifetime
{
    private const long Piece = 64 * 1024;

    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static byte[] Data(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Records every request ("PUT api/upload/…") with its body length, and can answer some itself.</summary>
    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage?>? answer = null) : DelegatingHandler(new SocketsHttpHandler())
    {
        private const string PutPiece = "PUT api/upload/";
        private readonly List<(string Request, long? Length)> _entries = [];

        public List<string> Requests
        {
            get
            {
                lock (_entries)
                    return _entries.Select(e => e.Request).ToList();
            }
        }

        /// <summary>The length of every piece sent, in order.</summary>
        public List<long> PieceLengths => Lengths(_entries);

        /// <summary>The pieces sent after the first status check, that is after a broken connection.</summary>
        public List<long> PieceLengthsAfterResume
        {
            get
            {
                lock (_entries)
                    return Lengths(_entries.SkipWhile(e => e.Request != "GET api/upload/").ToList());
            }
        }

        private List<long> Lengths(List<(string Request, long? Length)> entries)
        {
            lock (_entries)
                return entries.Where(e => e.Request == PutPiece).Select(e => e.Length ?? -1).ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var name = $"{request.Method} {(path.StartsWith("api/upload/", StringComparison.Ordinal) ? "api/upload/" + path.Split('/').Skip(3).FirstOrDefault() : path)}";
            lock (_entries)
                _entries.Add((name, request.Content?.Headers.ContentLength));
            return answer?.Invoke(request) ?? await base.SendAsync(request, cancellationToken);
        }

        public int Count(string request)
        {
            lock (_entries)
                return _entries.Count(e => e.Request == request);
        }
    }

    /// <summary>A client with fixed pieces of <see cref="Piece"/> bytes, or growing from a quarter of it with <paramref name="minPiece"/>.</summary>
    private PairnetsApiClient Client(HttpMessageHandler handler, Uri? url = null, long minPiece = Piece) =>
        new(url ?? _server.Url, _server.Token, "pc", handler, stallTimeout: TimeSpan.FromSeconds(20), pieceSize: Piece, minPieceSize: minPiece);

    [Fact]
    public async Task ABigFileGoesInPiecesAndArrivesWhole()
    {
        var data = Data((int)(Piece * 4 + 1000));
        var recorder = new Recorder();
        using var api = Client(recorder);
        long lastProgress = 0;

        var (result, sentHash, sentBytes) = await api.UploadAsync("big.bin", ContentHash.NoneBase, 1700000000000, new MemoryStream(data), p => lastProgress = p, default);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        Assert.Equal(ContentHash.Of(data), result.Entry!.Hash);
        Assert.Equal(data.Length, sentBytes);
        Assert.Equal(data.Length, lastProgress);
        Assert.Equal(1, recorder.Count("POST api/upload"));
        Assert.Equal(5, recorder.Count("PUT api/upload/"));
        Assert.Equal(1, recorder.Count("POST api/upload/commit"));
        Assert.Equal(0, recorder.Count("PUT api/file"));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "big.bin")));
        Assert.Equal(0, _server.Uploads.Count);
    }

    [Fact]
    public async Task ASmallFileStaysOneRequest()
    {
        var recorder = new Recorder();
        using var api = Client(recorder);
        var (result, _, _) = await api.UploadAsync("small.bin", ContentHash.NoneBase, 1700000000000, new MemoryStream(Data((int)Piece)), null, default);
        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(["PUT api/file"], recorder.Requests);
    }

    [Fact]
    public async Task AConflictAtTheStartIsReportedLikeOnePut()
    {
        using var api = Client(new Recorder());
        await api.UploadAsync("a.bin", ContentHash.NoneBase, 0, new MemoryStream(Data(10)), null, default);
        var (result, sentHash, _) = await api.UploadAsync("a.bin", ContentHash.NoneBase, 0, new MemoryStream(Data((int)Piece * 2)), null, default);
        Assert.Equal(ApiOutcome.Conflict, result.Outcome);
        Assert.Equal(string.Empty, sentHash);
    }

    [Fact]
    public async Task AnOlderServerGetsTheWholeFileInOneRequest()
    {
        // A server from before uploads in pieces has no /api/upload (plain 404 from routing).
        var recorder = new Recorder(req => req.RequestUri!.AbsolutePath.StartsWith("/api/upload", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : null);
        using var api = Client(recorder);
        var data = Data((int)Piece * 3);

        var (result, sentHash, _) = await api.UploadAsync("a.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default);
        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        await api.UploadAsync("b.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default);
        Assert.Equal(["POST api/upload", "PUT api/file", "PUT api/file"], recorder.Requests);
    }

    [Fact]
    public async Task ACutConnectionCostsOnlyThePieceInFlight()
    {
        await using var proxy = new ChaosProxy(_server.Port);
        var recorder = new Recorder();
        using var api = Client(recorder, proxy.Url);
        var data = Data((int)(Piece * 8));
        proxy.CutAfterUpstreamBytes = Piece * 5 / 2; // in the middle of the third piece
        using var heal = new CancellationTokenSource();
        var healer = Task.Run(async () =>
        {
            while (Volatile.Read(ref proxy.Cuts) == 0 && !heal.IsCancellationRequested)
                await Task.Delay(10);
            proxy.CutAfterUpstreamBytes = null;
        });

        var (result, sentHash, _) = await api.UploadAsync("a.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default);
        heal.Cancel();
        await healer;

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        Assert.True(proxy.Cuts > 0);
        Assert.Equal(1, recorder.Count("POST api/upload"));
        Assert.True(recorder.Count("GET api/upload/") >= 1, string.Join(", ", recorder.Requests));
        // Only the broken piece was sent again, not the file from the start.
        Assert.InRange(recorder.Count("PUT api/upload/"), 9, 10);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "a.bin")));
    }

    [Fact]
    public async Task PiecesGrowOnAFastConnection()
    {
        var recorder = new Recorder();
        using var api = Client(recorder, minPiece: Piece / 8);
        var data = Data((int)(Piece * 5 - 100));

        var (result, sentHash, _) = await api.UploadAsync("a.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        var pieces = recorder.PieceLengths;
        // A quarter of the largest piece first, then twice as big after each fast piece, up to the largest.
        Assert.Equal([Piece / 4, Piece / 2, Piece], pieces.Take(3));
        Assert.All(pieces, length => Assert.InRange(length, 1, Piece));
        Assert.Equal(data.Length, pieces.Sum());
        Assert.Equal(Piece, api.Pieces.Next);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "a.bin")));
    }

    [Fact]
    public async Task AfterACutThePiecesGetSmaller()
    {
        await using var proxy = new ChaosProxy(_server.Port);
        var recorder = new Recorder();
        using var api = Client(recorder, proxy.Url, minPiece: Piece / 8);
        var data = Data((int)(Piece * 8));
        // Pieces of 16, 32, 64 and 64 KiB get through; the cut comes in the fifth one.
        proxy.CutAfterUpstreamBytes = Piece * 3;
        using var heal = new CancellationTokenSource();
        var healer = Task.Run(async () =>
        {
            while (Volatile.Read(ref proxy.Cuts) == 0 && !heal.IsCancellationRequested)
                await Task.Delay(10);
            proxy.CutAfterUpstreamBytes = null;
        });

        var (result, sentHash, _) = await api.UploadAsync("a.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default);
        heal.Cancel();
        await healer;

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        Assert.True(proxy.Cuts > 0);
        Assert.Equal([Piece / 4, Piece / 2, Piece, Piece], recorder.PieceLengths.Take(4));
        // The first piece after the cut is half the size the cut one had.
        Assert.InRange(recorder.PieceLengthsAfterResume[0], 1, Piece / 2);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "a.bin")));
    }

    [Fact]
    public async Task CancellingDropsTheUploadOnTheServer()
    {
        using var api = Client(new Recorder());
        using var cts = new CancellationTokenSource();
        var data = Data((int)(Piece * 6));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.UploadAsync("a.bin", ContentHash.NoneBase, 0, new MemoryStream(data), p =>
            {
                if (p > Piece * 2)
                    cts.Cancel();
            }, cts.Token));

        Assert.Equal(0, _server.Uploads.Count);
        // The part file goes once the server notices the cut-off piece.
        for (var i = 0; i < 100 && Directory.GetFiles(_server.Paths.Tmp, "*.part").Length > 0; i++)
            await Task.Delay(50);
        Assert.Empty(Directory.GetFiles(_server.Paths.Tmp, "*.part"));
        Assert.Empty(_server.Store.ReadManifest(null).Entries);
    }

    [Fact]
    public async Task TwoComputersSyncABigFileThroughPieces()
    {
        await using var a = new Device("A", _server, pieceSize: Piece);
        await using var b = new Device("B", _server, pieceSize: Piece);
        var data = Data((int)(Piece * 5 + 123));
        await File.WriteAllBytesAsync(Path.Combine(a.Folder, "video.mp4"), data);
        File.SetLastWriteTimeUtc(Path.Combine(a.Folder, "video.mp4"), DateTime.UtcNow.AddMinutes(-5));

        await a.SyncUntilQuietAsync();
        await b.SyncUntilQuietAsync();

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(b.Folder, "video.mp4")));
        Assert.Equal(0, _server.Uploads.Count);
    }
}

/// <summary>Reading pieces and hashing each byte once, also when a piece is read again.</summary>
public class PieceReaderTests
{
    [Fact]
    public async Task EveryByteIsHashedOnceEvenWhenAPieceIsReadAgain()
    {
        var data = new byte[1000];
        new Random(3).NextBytes(data);
        using var reader = new PieceReader(new MemoryStream(data));
        var sink = new MemoryStream();

        reader.StartPiece(0, 600);
        await reader.CopyToAsync(sink);
        reader.StartPiece(250, 750); // the server had only 250 bytes of that piece
        await reader.CopyToAsync(sink);

        Assert.Equal(1000, reader.HashedUpTo);
        Assert.Equal(ContentHash.Of(data), reader.GetHash());
        Assert.Equal(data[250..], sink.ToArray()[600..]);
    }

    [Fact]
    public void ItCannotSkipBytesItHasNotRead()
    {
        using var reader = new PieceReader(new MemoryStream(new byte[100]));
        Assert.Throws<PairnetsProtocolException>(() => reader.StartPiece(50, 50));
        Assert.Throws<InvalidOperationException>(() => reader.GetHash());
    }

    [Fact]
    public async Task AFileThatGotShorterIsALocalReadError()
    {
        var file = new MemoryStream(new byte[100]);
        using var reader = new PieceReader(file);
        file.SetLength(40);
        reader.StartPiece(0, 100);
        await Assert.ThrowsAsync<IOException>(() => reader.CopyToAsync(Stream.Null));
    }

    [Fact]
    public void TheUploadStartsAtTheStreamsPosition()
    {
        var file = new MemoryStream(Encoding.UTF8.GetBytes("skip:payload"));
        file.Position = 5;
        using var reader = new PieceReader(file);
        Assert.Equal(7, reader.Size);
        reader.StartPiece(0, 7);
        var buffer = new byte[7];
        Assert.Equal(7, reader.Read(buffer, 0, 7));
        Assert.Equal("payload", Encoding.UTF8.GetString(buffer));
        Assert.Equal(ContentHash.Of(Encoding.UTF8.GetBytes("payload")), reader.GetHash());
    }
}
