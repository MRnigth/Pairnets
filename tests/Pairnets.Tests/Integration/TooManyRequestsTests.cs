using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// "Too many requests" (429) from the Pairnets service in front of the server: the apps wait as it says, go on one at a
/// time, send the same file again, and count nothing as failed (the owner's first sync of 38,206 files lost 37 to this).
/// </summary>
public class TooManyRequestsTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>
    /// Stands in for sync.pairnets.app: answers 429 rate_limited with Retry-After to the requests <c>refuse</c> picks (by
    /// their number, from 1) and passes the rest on to the real server. Logs when each request arrived and how many ran at once.
    /// </summary>
    private sealed class BusyService(Func<HttpRequestMessage, int, bool> refuse, Func<HttpResponseMessage>? answer = null) : DelegatingHandler(new SocketsHttpHandler())
    {
        private int _count;
        private int _running;

        public List<(DateTimeOffset At, string Request, int Status, int Running)> Log { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            var running = Interlocked.Increment(ref _running);
            var at = DateTimeOffset.UtcNow;
            var name = $"{request.Method} {request.RequestUri!.AbsolutePath.TrimStart('/')}";
            try
            {
                if (refuse(request, n))
                {
                    lock (Log)
                        Log.Add((at, name, 429, running));
                    return answer?.Invoke() ?? RateLimited();
                }
                var resp = await base.SendAsync(request, cancellationToken);
                lock (Log)
                    Log.Add((at, name, (int)resp.StatusCode, running));
                return resp;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>The service's answer, exactly as cloud/src/http.ts writes it.</summary>
    private static HttpResponseMessage RateLimited(string retryAfter = "1")
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"rate_limited","message":"Too many requests. Wait a little and try again."}""", Encoding.UTF8, "application/json"),
        };
        resp.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return resp;
    }

    [Fact]
    public async Task AFirstSyncThatHitsTheLimitWaitsGoesOnOneAtATimeAndLosesNothing()
    {
        // Uploads 6 to 10 are refused (the window is full), like the owner's sync after 383 files.
        var service = new BusyService((req, n) => req.Method == HttpMethod.Put && n is >= 6 and <= 10);
        using var api = new PairnetsApiClient(_server.Url, _server.Token, "PC-1", service, stallTimeout: TimeSpan.FromSeconds(20));
        var heard = new List<DateTimeOffset?>();
        api.Pressure.Changed += until =>
        {
            lock (heard)
                heard.Add(until);
        };
        using var folder = new TempDir("busy");
        using var stateDir = new TempDir("busy-state");
        for (var i = 0; i < 40; i++)
            await File.WriteAllTextAsync(Path.Combine(folder.Path, $"photo-{i:00}.txt"), "small file " + i);
        using var state = new StateDb(Path.Combine(stateDir.Path, "state.db"));
        var engine = new SyncEngine(new EngineOptions { Folder = folder.Path, DeviceName = "PC-1", StabilityWindow = TimeSpan.Zero, MaxParallelTransfers = 4 }, api, state);

        var result = await engine.RunPassAsync(new PassOptions("first sync"), CancellationToken.None);

        Assert.Equal(PassOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Errors); // nothing failed...
        Assert.Equal(40, result.Uploaded); // ...and every file arrived, the refused ones too
        Assert.Equal(40, (await _server.Client().GetManifestAsync(null, CancellationToken.None)).Entries.Count(e => !e.Deleted));

        var log = service.Log.OrderBy(e => e.At).ToList();
        Assert.Equal(5, log.Count(e => e.Status == 429));
        // While held, nothing new went out (requests already on their way when the first 429 came may still arrive)...
        var first = log.First(e => e.Status == 429).At;
        Assert.DoesNotContain(log, e => e.At > first + TimeSpan.FromMilliseconds(100) && e.At < first + TimeSpan.FromMilliseconds(900));
        // ...and afterwards they went one at a time.
        Assert.All(log.Where(e => e.At > first + TimeSpan.FromSeconds(1)), e => Assert.Equal(1, e.Running));

        // The app said it was slowed down, and that it went on again.
        await WaitUntil(() => { lock (heard) return heard.Count >= 2 && heard[^1] is null; });
        Assert.NotNull(heard[0]);
    }

    [Fact]
    public async Task ASingleFileIsSentAgainFromItsStartAndTheTimeoutDoesNotRunWhileItWaits()
    {
        var refused = 0;
        var service = new BusyService((req, n) => req.Method == HttpMethod.Put && Interlocked.Increment(ref refused) <= 2, () => RateLimited("2"));
        // A stall timeout shorter than the wait: the wait must not count against it.
        using var api = new PairnetsApiClient(_server.Url, _server.Token, "PC-1", service, stallTimeout: TimeSpan.FromSeconds(1.5));
        var data = Encoding.UTF8.GetBytes("the same bytes, sent again");
        long lastProgress = -1;

        var (result, hash, sent) = await api.UploadAsync("again.txt", Pairnets.Core.Hashing.ContentHash.NoneBase, 1_700_000_000_000, new MemoryStream(data), p => lastProgress = p, CancellationToken.None);

        Assert.Equal(ApiOutcome.Ok, result.Outcome);
        Assert.Equal(Pairnets.Core.Hashing.ContentHash.Of(data), hash);
        Assert.Equal(data.Length, sent);
        Assert.Equal(data.Length, lastProgress);
        Assert.Equal(3, service.Log.Count(e => e.Request == "PUT api/file"));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_server.Paths.Files, "again.txt")));
    }

    [Fact]
    public async Task TheNestsOwnTooManyStaysAnAnswerTheServicesIsWaitedOut()
    {
        // The nest's own 429 on joining ("too many computers waiting") is its answer, said as it is: no waiting, no retry.
        var own = new BusyService((req, _) => req.RequestUri!.AbsolutePath.EndsWith("pair/start", StringComparison.Ordinal), () =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"code":"bad-request","message":"Too many computers are waiting to be approved. Try again in a few minutes."}""", Encoding.UTF8, "application/json"),
            });
        using (var api = new PairnetsApiClient(_server.Url, string.Empty, "PC-3", own))
        {
            var refusal = await Assert.ThrowsAsync<PairnetsProtocolException>(() => api.StartPairingAsync(new PairStartRequest("PC-3", "Windows", "1.0.90"), CancellationToken.None));
            Assert.Equal("Too many computers are waiting to be approved. Try again in a few minutes.", refusal.Message);
            Assert.Single(own.Log);
            Assert.Null(api.Pressure.HeldUntil);
        }

        // The service's rate_limited on the same call is waited out and sent again; then the nest answers for itself.
        var service = new BusyService((req, n) => n == 1);
        using (var api = new PairnetsApiClient(_server.Url, string.Empty, "PC-3", service))
        {
            var answer = await Record.ExceptionAsync(() => api.StartPairingAsync(new PairStartRequest("PC-3", "Windows", "1.0.90"), CancellationToken.None));
            Assert.DoesNotContain("Too many", answer?.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(new[] { 429, 0 }, service.Log.Select(e => e.Status == 429 ? 429 : 0));
        }
    }

    [Fact]
    public async Task AServiceThatNeverLetsUpEndsThePassAsOfflineNotAsFailedFiles()
    {
        var service = new BusyService((req, _) => true);
        using var api = new PairnetsApiClient(_server.Url, _server.Token, "PC-1", service) { MaxTooManyRetries = 3 }; // 30 in the apps

        var failure = await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetManifestAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1)));
        Assert.Equal(ServiceErrors.RateLimited, failure.Code); // the pass ends as "offline" and the runner tries again later
        Assert.Equal($"Too many requests to {_server.Url.Host}; Pairnets keeps trying.", failure.Message);
        Assert.Equal(3, service.Log.Count);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            await Task.Delay(50);
        }
    }
}
