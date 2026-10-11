using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pairnets.Core.Api;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// "429 Too many requests" (the Pairnets service's limit per address): every request waits the time it asked for, and
/// the files go again instead of being counted as failed.
/// </summary>
public class SlowDownTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>What the service answers when the limit is reached.</summary>
    private static HttpResponseMessage TooMany(TimeSpan retryAfter) => new(HttpStatusCode.TooManyRequests)
    {
        Headers = { RetryAfter = new RetryConditionHeaderValue(retryAfter) },
        Content = JsonContent.Create(new { error = "rate_limited", message = "Too many requests. Wait a little and try again." }),
    };

    [Fact]
    public async Task EveryTransferWaitsAndTheFilesGoAgainInsteadOfFailing()
    {
        var log = new RequestLog();
        var refused = 0;
        long refusedAtMs = 0;
        var clock = Stopwatch.StartNew();
        var sentMs = new List<long>();
        log.OnRequest = _ =>
        {
            lock (sentMs)
                sentMs.Add(clock.ElapsedMilliseconds);
        };
        log.Answer = req =>
        {
            if (req.Method != HttpMethod.Put || req.RequestUri!.AbsolutePath != "/api/file" || Interlocked.Increment(ref refused) > 3)
                return null;
            Interlocked.CompareExchange(ref refusedAtMs, clock.ElapsedMilliseconds, 0);
            return TooMany(TimeSpan.FromSeconds(1));
        };
        await using var pc = new Device("pc", _server, requests: log, parallel: 4);
        var holds = new List<DateTimeOffset>();
        pc.Api.Pressure.Changed += until =>
        {
            if (until is { } u)
                lock (holds)
                    holds.Add(u);
        };
        for (var i = 0; i < 20; i++)
            pc.Write($"f{i:00}.txt", "file " + i);

        var result = await pc.SyncAsync();

        Assert.Equal(PassOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Errors);
        Assert.Equal(20, result.Uploaded);
        Assert.Equal(20, _server.Store.ReadManifest(null).Entries.Count);
        lock (holds)
            Assert.NotEmpty(holds); // every request was held
        // Nothing was sent while the wait ran (a little slack for requests already on their way).
        var first = Interlocked.Read(ref refusedAtMs);
        lock (sentMs)
            Assert.DoesNotContain(sentMs, t => t > first + 100 && t < first + 900);
    }

    [Fact]
    public async Task ABigUploadThatIsToldToSlowDownGoesOnWhereItWas()
    {
        var log = new RequestLog();
        var pieces = 0;
        log.Answer = req => req.Method == HttpMethod.Put && req.RequestUri!.AbsolutePath.StartsWith("/api/upload/", StringComparison.Ordinal)
            && Interlocked.Increment(ref pieces) == 3
                ? TooMany(TimeSpan.FromSeconds(1))
                : null;
        await using var pc = new Device("pc", _server, requests: log, pieceSize: 64 * 1024);
        var data = new byte[64 * 1024 * 6];
        new Random(3).NextBytes(data);
        await File.WriteAllBytesAsync(Path.Combine(pc.Folder, "big.bin"), data);

        var result = await pc.SyncAsync();

        Assert.Equal(PassOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Errors);
        Assert.Equal(1, log.Count("POST api/upload")); // the same upload went on, not a new one
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "big.bin")));
    }

    [Fact]
    public void RetryAfterIsRead()
    {
        // The limits (at least 1 s, at most 10 min, a growing default without one) are BackPressure's: see SyncFeedbackTests.
        Assert.Equal(TimeSpan.FromSeconds(60), PairnetsApiClient.RetryAfterOf(TooMany(TimeSpan.FromSeconds(60))));
        Assert.Null(PairnetsApiClient.RetryAfterOf(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
    }
}
