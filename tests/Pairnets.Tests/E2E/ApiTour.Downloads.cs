using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Crypto;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

public sealed partial class ApiTour
{
    /// <summary>
    /// Downloads on the nest (1.0.61.5). On the tour's own nest the walk takes the routes through what the nest refuses (not
    /// a web link, an address inside its own network, a download it doesn't know): an installed nest can't reach a test
    /// website, and never downloads from this computer's own addresses anyway. The real downloads run on a second nest
    /// inside the test process, with a test website, like <see cref="FriendsAsync"/>. NestDownloadTests covers every case.
    /// </summary>
    private async Task NestDownloadsAsync()
    {
        Step("Downloads on the nest: the list, and the links it refuses");
        await Refused(HttpMethod.Get, "api/downloads");
        var list = await Read<NestDownloadList>(Get(_laptopApi, "api/downloads"));
        Assert.Equal(NestDownloadRules.AtOnce, list.AtOnce);
        Assert.Empty(list.Downloads);
        await Refused(HttpMethod.Post, "api/downloads", json: new NestDownloadRequest("https://example.com/a.iso"));
        Assert.Equal(NestDownloadErrors.NotWeb, (await Read<ErrorBody>(Post(_laptopApi, "api/downloads",
            new NestDownloadRequest("file:///etc/passwd"), HttpStatusCode.BadRequest))).Code);
        Assert.Equal(NestDownloadErrors.Blocked, (await Read<ErrorBody>(Post(_laptopApi, "api/downloads",
            new NestDownloadRequest("http://169.254.169.254/latest/meta-data/"), HttpStatusCode.BadRequest))).Code);
        Assert.Equal(NestDownloadErrors.Blocked, (await Read<ErrorBody>(Post(_laptopApi, "api/downloads",
            new NestDownloadRequest("http://127.0.0.1:5075/api/health"), HttpStatusCode.BadRequest))).Code);
        await Refused(HttpMethod.Delete, "api/downloads/0123456789abcdef");
        await Call(_laptopApi, HttpMethod.Delete, "api/downloads/0123456789abcdef", expect: HttpStatusCode.NotFound);
        await Refused(HttpMethod.Post, "api/downloads/0123456789abcdef/retry");
        await Post(_laptopApi, "api/downloads/0123456789abcdef/retry", expect: HttpStatusCode.NotFound);
        await Refused(HttpMethod.Get, "api/downloads/0123456789abcdef/file");
        await Get(_laptopApi, "api/downloads/0123456789abcdef/file", HttpStatusCode.NotFound);

        Step("A nest downloads a link from a website, tries a missing one again, and clears them");
        await using var site = await FakeWebsite.StartAsync();
        site.Files["tour.iso"] = new byte[100_000];
        async Task<(TestServer Nest, HttpClient Http)> NestAsync()
        {
            var started = await TestServer.StartAsync(configureBuilder: builder =>
                builder.Services.AddSingleton<IStartupFilter>(new AnsweredRoutesFilter(Record.ServerSaw)));
            var client = new HttpClient(new RecordingHandler(Record, new SocketsHttpHandler { UseProxy = false })) { BaseAddress = started.Url };
            client.DefaultRequestHeaders.Add(PairnetsHeaders.Token, started.Token);
            _clients.Add(client);
            return (started, client);
        }
        var (nest, http) = await NestAsync();
        await using var plainNest = nest;
        async Task<NestDownload> Ended(string id)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var d = (await Read<NestDownloadList>(Get(http, "api/downloads"))).Downloads.Single(x => x.Id == id);
                if (!d.IsBusy)
                    return d;
                Assert.True(DateTime.UtcNow < deadline, $"The download {d.Name} didn't end: {d.State}, {d.Bytes} bytes.");
                await Task.Delay(100);
            }
        }

        var done = await Ended((await Read<NestDownload>(Post(http, "api/downloads", new NestDownloadRequest(site.Url("files/tour.iso").ToString())))).Id);
        Assert.Equal("Downloads/tour.iso", done.Path);
        var opened = await nest.Store.OpenReadAsync("Downloads/tour.iso", CancellationToken.None);
        Assert.NotNull(opened);
        await opened.Stream.DisposeAsync();
        var missing = await Ended((await Read<NestDownload>(Post(http, "api/downloads", new NestDownloadRequest(site.Url("files/later.iso").ToString())))).Id);
        Assert.Equal(NestDownloadErrors.NotFound, missing.Error);
        site.Files["later.iso"] = new byte[1000];
        await Post(http, $"api/downloads/{missing.Id}/retry", expect: HttpStatusCode.NoContent);
        Assert.Equal(NestDownload.Done, (await Ended(missing.Id)).State);
        await Call(http, HttpMethod.Delete, $"api/downloads/{done.Id}", expect: HttpStatusCode.NoContent);
        await Call(http, HttpMethod.Delete, $"api/downloads/{missing.Id}", expect: HttpStatusCode.NoContent);

        Step("On an encrypted nest the download waits in the holding spot, not end-to-end encrypted");
        (nest, http) = await NestAsync(); // a nest is encrypted while it is still empty
        await using var sealedNest = nest;
        using var keys = ComputerKeys.Create();
        var ring = KeyRing.Create(nest.Store.ServerId, keys.AsTrusted("TOUR-PC"));
        using (var encrypts = nest.RawHttp(nest.Token))
        {
            encrypts.DefaultRequestHeaders.Add(PairnetsHeaders.Features, PairnetsHeaders.SealedFeature);
            Assert.Equal(HttpStatusCode.NoContent, (await encrypts.PutAsync("api/keys/recovery",
                new ByteArrayContent(Envelope.SealForRecovery(ring, RecoveryKey.Create())))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await encrypts.PutAsync($"api/keys/envelopes/{keys.Id}",
                new ByteArrayContent(Envelope.SealForComputer(ring, keys.AgreementPublicKey, keys)))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await encrypts.PostAsync("api/keys/seal", null)).StatusCode);
        }
        var held = await Ended((await Read<NestDownload>(Post(http, "api/downloads", new NestDownloadRequest(site.Url("files/tour.iso").ToString())))).Id);
        Assert.True(held.Plain);
        Assert.Equal(100_000, (await (await Get(http, $"api/downloads/{held.Id}/file")).Content.ReadAsByteArrayAsync()).Length);
    }
}
