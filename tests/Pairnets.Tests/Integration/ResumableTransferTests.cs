using System.Net.Http.Json;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Big transfers that are cut off continue where they stopped: after an outage longer than the retries within
/// one pass, after Pause and Resume, and after a restart of the app. They start again when the file changed in
/// between, and two versions of a file are never mixed.
/// </summary>
public class ResumableTransferTests : IAsyncLifetime
{
    private const long Piece = 64 * 1024;
    private const int Size = (int)(Piece * 16); // 1 MiB, in 16 pieces

    private TestServer _server = null!;
    private ChaosProxy _proxy = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _proxy = new ChaosProxy(_server.Port);
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static byte[] Data(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Short retries within a pass, so an outage outlasts them quickly.</summary>
    private static void QuickRetries(PairnetsApiClient api)
    {
        api.PieceRetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)];
        api.BusyPollInterval = TimeSpan.FromMilliseconds(100);
        api.BusyTimeout = TimeSpan.FromSeconds(20);
    }

    private Device Uploader(RequestLog log, Uri? url = null) =>
        new("desktop", _server, url: url ?? _proxy.Url, pieceSize: Piece, requests: log, configureApi: QuickRetries);

    /// <summary>Writes a file last changed ten minutes ago (as a finished video would be), so its hash is cached.</summary>
    private static DateTime WriteOld(Device d, string rel, byte[] data)
    {
        d.WriteBytes(rel, data);
        var mtime = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(d.Full(rel), mtime);
        return mtime;
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Runs a pass during which the network goes down after <paramref name="bytes"/> went up and stays down past the
    /// retries: the pass ends offline, the server keeps part of the file, and state.db remembers the upload.
    /// Returns how many bytes the server has.
    /// </summary>
    private async Task<long> UploadUntilOutageAsync(Device d, string path, long bytes)
    {
        _proxy.GoDownAfterUpstreamBytes = bytes;
        var r = await d.SyncAsync();
        Assert.Equal(PassOutcome.Offline, r.Outcome);
        Assert.True(_proxy.IsDown);
        var saved = d.State.GetUpload(path);
        Assert.NotNull(saved);
        await WaitUntil(() => _server.Uploads.Status(saved!.UploadId) is { Busy: false }, "the server to notice the cut");
        var received = _server.Uploads.Received(saved!.UploadId)!.Value;
        Assert.InRange(received, Piece, Size - 1);
        Assert.Null(_server.Store.Manifest.Get(path));
        return received;
    }

    private void AssertNoUploadsLeft()
    {
        Assert.Equal(0, _server.Uploads.Count);
        Assert.Empty(Directory.GetFiles(_server.Paths.Tmp, "*.part"));
    }

    // ------------------------------------------------------------------ uploads

    [Fact]
    public async Task AnOutageLongerThanTheRetriesContinuesFromTheServersCount()
    {
        var log = new RequestLog();
        await using var desktop = Uploader(log);
        var data = Data(Size, 1);
        WriteOld(desktop, "video.mp4", data);
        var before = await UploadUntilOutageAsync(desktop, "video.mp4", Size / 2);

        _proxy.Heal();
        var mark = log.Mark;
        var r = await desktop.SyncAsync();

        Assert.Equal(1, r.Uploaded);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "video.mp4")));
        Assert.Equal(ContentHash.Of(data), _server.Store.Manifest.Get("video.mp4")!.Hash);
        // The same upload went on: only what the server did not have was sent again.
        Assert.Equal(0, log.Entries.Skip(mark).Count(e => e.Request == "POST api/upload"));
        Assert.Equal(Size - before, log.PieceBytesSince(mark));
        Assert.Equal(0, log.Count("DELETE api/upload/"));
        Assert.Null(desktop.State.GetUpload("video.mp4"));
        AssertNoUploadsLeft();
    }

    [Fact]
    public async Task ARestartOfTheAppContinuesTheUpload()
    {
        var log = new RequestLog();
        await using var desktop = Uploader(log);
        var data = Data(Size, 2);
        WriteOld(desktop, "video.mp4", data);
        var before = await UploadUntilOutageAsync(desktop, "video.mp4", Size / 3);
        var id = desktop.State.GetUpload("video.mp4")!.UploadId;

        await desktop.RestartAppAsync(); // nothing in memory survives; state.db does
        Assert.Equal(id, desktop.State.GetUpload("video.mp4")!.UploadId);
        _proxy.Heal();
        var mark = log.Mark;
        var r = await desktop.SyncAsync();

        Assert.Equal(1, r.Uploaded);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "video.mp4")));
        Assert.Equal(Size - before, log.PieceBytesSince(mark));
        Assert.Equal(0, log.Entries.Skip(mark).Count(e => e.Request == "POST api/upload"));
        Assert.Null(desktop.State.GetUpload("video.mp4"));
        AssertNoUploadsLeft();
    }

    [Fact]
    public async Task PauseAndResumeContinueTheUpload()
    {
        var log = new RequestLog();
        await using var desktop = Uploader(log, _server.Url);
        var data = Data(Size, 3);
        WriteOld(desktop, "big.bin", data);

        var pieces = 0;
        SyncRunner? runner = null;
        var paused = new TaskCompletionSource();
        log.OnRequest = req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.StartsWith("/api/upload/", StringComparison.Ordinal)
                && Interlocked.Increment(ref pieces) == 6)
            {
                runner!.Pause(); // the user clicks Pause while the sixth piece is on its way
                paused.TrySetResult();
            }
        };
        runner = desktop.StartRunner();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntil(() => desktop.State.GetUpload("big.bin") is { } u && _server.Uploads.Status(u.UploadId) is { Busy: false }, "the paused upload to settle");
        await Task.Delay(500);
        var id = desktop.State.GetUpload("big.bin")!.UploadId;
        var received = _server.Uploads.Received(id)!.Value;
        Assert.InRange(received, 5 * Piece, Size - 1);
        Assert.Null(_server.Store.Manifest.Get("big.bin"));
        Assert.Equal(0, log.Count("DELETE api/upload/"));

        log.OnRequest = null;
        var mark = log.Mark;
        runner.Resume();
        await WaitUntil(() => _server.Store.Manifest.Get("big.bin") is { Deleted: false }, "the upload to finish after Resume");

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "big.bin")));
        Assert.InRange(log.PieceBytesSince(mark), 1, Size - received);
        Assert.Equal(0, log.Entries.Skip(mark).Count(e => e.Request == "POST api/upload"));
        await WaitUntil(() => desktop.State.GetUpload("big.bin") is null, "the finished upload to be forgotten");
        AssertNoUploadsLeft();
    }

    [Fact]
    public async Task AFileChangedInBetweenIsSentFromTheStart()
    {
        var log = new RequestLog();
        await using var desktop = Uploader(log);
        WriteOld(desktop, "doc.bin", Data(Size, 4));
        await UploadUntilOutageAsync(desktop, "doc.bin", Size / 2);
        var oldId = desktop.State.GetUpload("doc.bin")!.UploadId;

        var changed = Data(Size, 5);
        WriteOld(desktop, "doc.bin", changed);
        File.SetLastWriteTimeUtc(desktop.Full("doc.bin"), DateTime.UtcNow.AddMinutes(-5)); // a newer edit
        _proxy.Heal();
        var mark = log.Mark;
        var r = await desktop.SyncAsync();

        Assert.Equal(1, r.Uploaded);
        Assert.Equal(changed, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "doc.bin")));
        Assert.Equal(Size, log.PieceBytesSince(mark));
        Assert.Equal(1, log.Entries.Skip(mark).Count(e => e.Request == "POST api/upload"));
        Assert.Equal(1, log.Count("DELETE api/upload/")); // the old part was given up for good
        Assert.Null(_server.Uploads.Status(oldId));
        AssertNoUploadsLeft();
    }

    [Fact]
    public async Task AChangeThatKeepsSizeAndTimeIsRefusedInsteadOfMixed()
    {
        var log = new RequestLog();
        await using var desktop = Uploader(log);
        var original = Data(Size, 6);
        var mtime = WriteOld(desktop, "db.bin", original);
        await UploadUntilOutageAsync(desktop, "db.bin", Size / 2);

        // A program rewrites the start of the file and puts the old time back: size and time say "unchanged".
        var rewritten = (byte[])original.Clone();
        for (var i = 0; i < 1000; i++)
            rewritten[i] ^= 0x5A;
        desktop.WriteBytes("db.bin", rewritten);
        File.SetLastWriteTimeUtc(desktop.Full("db.bin"), mtime);
        _proxy.Heal();

        // The upload continues, but the whole file is hashed again: the server's copy (old start, new rest)
        // does not match, so the commit is refused and nothing is stored.
        var r = await desktop.SyncAsync();
        Assert.Equal(0, r.Uploaded);
        Assert.Equal(1, r.Errors);
        Assert.Null(_server.Store.Manifest.Get("db.bin"));
        Assert.Null(desktop.State.GetUpload("db.bin"));
        AssertNoUploadsLeft();

        // The next pass sends the file as it is now, from the start.
        r = await desktop.SyncAsync();
        Assert.Equal(1, r.Uploaded);
        Assert.Equal(rewritten, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "db.bin")));
        Assert.Equal(ContentHash.Of(rewritten), _server.Store.Manifest.Get("db.bin")!.Hash);
        Assert.Equal(0, (await desktop.SyncAsync()).Changes);
    }

    [Fact]
    public async Task AnUploadTheServerNoLongerHasStartsAgain()
    {
        var log = new RequestLog();
        await using var desktop = Uploader(log);
        var data = Data(Size, 7);
        WriteOld(desktop, "a.bin", data);
        await UploadUntilOutageAsync(desktop, "a.bin", Size / 2);
        _server.Uploads.Abort(desktop.State.GetUpload("a.bin")!.UploadId); // dropped after an hour without data

        _proxy.Heal();
        var mark = log.Mark;
        var r = await desktop.SyncAsync();

        Assert.Equal(1, r.Uploaded);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "a.bin")));
        Assert.Equal(Size, log.PieceBytesSince(mark));
        Assert.Equal(1, log.Entries.Skip(mark).Count(e => e.Request == "POST api/upload"));
        Assert.Null(desktop.State.GetUpload("a.bin"));
        AssertNoUploadsLeft();
    }

    [Fact]
    public async Task AnUploadThatIsNoLongerNeededIsGivenUp()
    {
        var log = new RequestLog();
        await using var desktop = Uploader(log);
        WriteOld(desktop, "gone.bin", Data(Size, 8));
        await UploadUntilOutageAsync(desktop, "gone.bin", Size / 2);

        desktop.Delete("gone.bin");
        _proxy.Heal();
        var r = await desktop.SyncAsync();

        Assert.Equal(PassOutcome.Completed, r.Outcome);
        Assert.Equal(1, log.Count("DELETE api/upload/"));
        Assert.Null(desktop.State.GetUpload("gone.bin"));
        Assert.Null(_server.Store.Manifest.Get("gone.bin"));
        AssertNoUploadsLeft();
    }

    [Fact]
    public async Task APieceStillArrivingIsWaitedForNotCountedAsAFailure()
    {
        using var http = _server.RawHttp(_server.Token);
        var data = Data(Size, 9);
        var start = await http.PostAsync($"api/upload?path=busy.bin&base=none&size={Size}", null);
        var id = (await start.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!.Id;
        // A piece of an earlier attempt whose connection went quiet: the server waits for it (2 s in tests).
        using var hold = new CancellationTokenSource();
        var stalled = http.PutAsync($"api/upload/{id}?offset=0", new StallingContent(data[..(int)Piece], hold.Token), hold.Token);
        await WaitUntil(() => _server.Uploads.Status(id) is { Busy: true, Received: > 0 }, "the stalled piece to arrive");

        var log = new RequestLog();
        using var api = new PairnetsApiClient(_server.Url, _server.Token, "pc", log.Handler(), pieceSize: Piece, minPieceSize: Piece)
        {
            PieceRetryDelays = [], // a single failure would end the attempt: waiting must not count as one
            BusyPollInterval = TimeSpan.FromMilliseconds(200),
        };
        var resume = new UploadResume(id);
        var (result, sentHash, _) = await api.UploadAsync("busy.bin", ContentHash.NoneBase, 0, new MemoryStream(data), null, default, resume);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(ContentHash.Of(data), sentHash);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "busy.bin")));
        Assert.True(log.Count("GET api/upload/") >= 2, string.Join(", ", log.Entries.Select(e => e.Request)));
        Assert.Equal(Piece, api.Pieces.Next); // nothing failed, so the pieces did not shrink
        Assert.Null(resume.Id);
        await hold.CancelAsync();
        try
        {
            await stalled;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
        }
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>The desktop stores the file; the laptop (through the proxy) loses the network half-way through downloading it.</summary>
    private async Task<(Device Desktop, Device Laptop, byte[] Data, long Kept)> DownloadUntilOutageAsync(RequestLog log, int seed)
    {
        var desktop = new Device("desktop", _server, pieceSize: Piece);
        var laptop = new Device("laptop", _server, url: _proxy.Url, requests: log, resumeDownloadsFrom: Piece);
        var data = Data(Size, seed);
        WriteOld(desktop, "movie.mkv", data);
        Assert.Equal(1, (await desktop.SyncAsync()).Uploaded);

        _proxy.GoDownAfterDownstreamBytes = Size / 2;
        var r = await laptop.SyncAsync();
        Assert.Equal(PassOutcome.Offline, r.Outcome);
        Assert.False(laptop.Exists("movie.mkv")); // nothing half-written at the real name
        var partial = laptop.State.GetDownload("movie.mkv");
        Assert.NotNull(partial);
        Assert.Equal(ContentHash.Of(data), partial!.Hash);
        var kept = new FileInfo(Path.Combine(laptop.Engine.TempDirectory, partial.TmpName)).Length;
        Assert.InRange(kept, 1, Size - 1);
        return (desktop, laptop, data, kept);
    }

    [Fact]
    public async Task AnInterruptedDownloadContinuesWithARangeAlsoAfterARestart()
    {
        var log = new RequestLog();
        var (desktop, laptop, data, kept) = await DownloadUntilOutageAsync(log, 10);
        await using (desktop)
        await using (laptop)
        {
            await laptop.RestartAppAsync();
            _proxy.Heal();
            var mark = log.Mark;
            var down = _proxy.DownstreamBytes;
            var r = await laptop.SyncAsync();

            Assert.Equal(1, r.Downloaded);
            Assert.Equal(data, await File.ReadAllBytesAsync(laptop.Full("movie.mkv")));
            Assert.Equal(ContentHash.Of(data), laptop.State.GetFile("movie.mkv")!.BaseHash);
            var get = Assert.Single(log.Entries.Skip(mark), e => e.Request == "GET api/file");
            Assert.Equal($"bytes={kept}-", get.Range);
            Assert.Equal($"\"{ContentHash.Of(data)}\"", get.IfRange);
            // Only the rest came over the network (plus the manifest and headers).
            Assert.InRange(_proxy.DownstreamBytes - down, Size - kept, Size - kept + 32 * 1024);
            Assert.Null(laptop.State.GetDownload("movie.mkv"));
            Assert.Empty(Directory.EnumerateFiles(laptop.Engine.TempDirectory));
        }
    }

    [Fact]
    public async Task ANewVersionOnTheServerStartsTheDownloadAgain()
    {
        var log = new RequestLog();
        var (desktop, laptop, _, _) = await DownloadUntilOutageAsync(log, 11);
        await using (desktop)
        await using (laptop)
        {
            var newer = Data(Size, 12);
            WriteOld(desktop, "movie.mkv", newer);
            File.SetLastWriteTimeUtc(desktop.Full("movie.mkv"), DateTime.UtcNow.AddMinutes(-1));
            Assert.Equal(1, (await desktop.SyncAsync()).Uploaded);

            _proxy.Heal();
            var mark = log.Mark;
            var r = await laptop.SyncAsync();

            Assert.Equal(1, r.Downloaded);
            Assert.Equal(newer, await File.ReadAllBytesAsync(laptop.Full("movie.mkv"))); // never the old start with the new end
            var get = Assert.Single(log.Entries.Skip(mark), e => e.Request == "GET api/file");
            Assert.Null(get.Range);
            Assert.Null(laptop.State.GetDownload("movie.mkv"));
            Assert.Empty(Directory.EnumerateFiles(laptop.Engine.TempDirectory));
        }
    }

    [Fact]
    public async Task ACorruptedPartialDownloadIsRejectedAndDownloadedAgain()
    {
        var log = new RequestLog();
        var (desktop, laptop, data, _) = await DownloadUntilOutageAsync(log, 13);
        await using (desktop)
        await using (laptop)
        {
            var partial = Path.Combine(laptop.Engine.TempDirectory, laptop.State.GetDownload("movie.mkv")!.TmpName);
            var bytes = await File.ReadAllBytesAsync(partial);
            bytes[10] ^= 0xFF; // a bad sector, or another program
            await File.WriteAllBytesAsync(partial, bytes);

            _proxy.Heal();
            var mark = log.Mark;
            var r = await laptop.SyncAsync();

            Assert.Equal(1, r.Downloaded);
            Assert.Equal(data, await File.ReadAllBytesAsync(laptop.Full("movie.mkv")));
            var gets = log.Entries.Skip(mark).Where(e => e.Request == "GET api/file").ToList();
            Assert.Equal(2, gets.Count);
            Assert.NotNull(gets[0].Range); // continued, failed the hash check...
            Assert.Null(gets[1].Range);    // ...so the whole file came again
            Assert.Empty(Directory.EnumerateFiles(laptop.Engine.TempDirectory));
        }
    }

    [Fact]
    public async Task APausedDownloadKeepsWhatArrived()
    {
        await using var desktop = new Device("desktop", _server, pieceSize: Piece);
        var log = new RequestLog();
        await using var laptop = new Device("laptop", _server, requests: log, resumeDownloadsFrom: Piece);
        var data = Data(Size, 14);
        WriteOld(desktop, "movie.mkv", data);
        await desktop.SyncAsync();
        laptop.Api.DownloadLimit.SetMegabytesPerSecond(0.1); // slow enough to pause half-way

        using var pause = new CancellationTokenSource();
        laptop.Engine.Progress += p =>
        {
            if (p.Operation == "download" && p.BytesDone > Size / 4)
                pause.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => laptop.Engine.RunPassAsync(new PassOptions("test"), pause.Token));
        var partial = laptop.State.GetDownload("movie.mkv");
        Assert.NotNull(partial);
        var kept = new FileInfo(Path.Combine(laptop.Engine.TempDirectory, partial!.TmpName)).Length;
        Assert.InRange(kept, 1, Size - 1);

        laptop.Api.DownloadLimit.SetMegabytesPerSecond(0);
        var mark = log.Mark;
        var r = await laptop.SyncAsync();
        Assert.Equal(1, r.Downloaded);
        Assert.Equal(data, await File.ReadAllBytesAsync(laptop.Full("movie.mkv")));
        Assert.Equal($"bytes={kept}-", Assert.Single(log.Entries.Skip(mark), e => e.Request == "GET api/file").Range);
    }

    [Fact]
    public async Task TheServerSendsTheWholeFileWhenItsVersionChanged()
    {
        using var api = _server.Client();
        var v1 = Data(300_000, 15);
        var v2 = Data(300_000, 16);
        await api.UploadAsync("f.bin", ContentHash.NoneBase, 0, new MemoryStream(v1), null, default);
        await api.UploadAsync("f.bin", ContentHash.Of(v1), 0, new MemoryStream(v2), null, default);

        // A partial download of v1, continued after the server moved on to v2.
        var destination = new MemoryStream();
        destination.Write(v1, 0, 100_000);
        var resume = new DownloadResume(ContentHash.Of(v1), 100_000);
        var dl = await api.DownloadAsync("f.bin", destination, null, default, resume);

        Assert.Equal(ContentHash.Of(v2), dl.Hash);
        Assert.Equal(v2, destination.ToArray());
        Assert.Equal(0, resume.ContinuedFrom);
        Assert.Equal(ContentHash.Of(v2), resume.ServedHash);

        // The same version continues where it stopped.
        destination = new MemoryStream();
        destination.Write(v2, 0, 100_000);
        resume = new DownloadResume(ContentHash.Of(v2), 100_000);
        dl = await api.DownloadAsync("f.bin", destination, null, default, resume);
        Assert.Equal(ContentHash.Of(v2), dl.Hash);
        Assert.Equal(v2, destination.ToArray());
        Assert.Equal(100_000, resume.ContinuedFrom);
    }

    /// <summary>A request body that sends some bytes and then goes quiet (a connection that died without a reset).</summary>
    private sealed class StallingContent(byte[] first, CancellationToken hold) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(first, hold);
            await stream.FlushAsync(hold);
            await Task.Delay(Timeout.Infinite, hold);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
