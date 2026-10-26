using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Crypto;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Server.Services;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// The nest downloads a link for you (1.0.61.8): the owner's computer hands over a link, the nest fetches it into its own
/// folders and the file syncs like any other. Only from the web (never the nest's own network), never past the free-space
/// line, carrying on after a dropped connection or a restart, and the half-done file goes on Cancel.
/// </summary>
public class NestDownloadTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private FakeWebsite _site = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _site = await FakeWebsite.StartAsync();
        _site.Files["a.bin"] = Bytes(300_000, 1);
        _owner = _server.RawHttp(_server.Token);
    }

    public async Task DisposeAsync()
    {
        _owner.Dispose();
        await _site.DisposeAsync();
        await _server.DisposeAsync();
    }

    private NestDownloader Downloader => _server.Services.GetRequiredService<NestDownloader>();

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private async Task<HttpResponseMessage> PostAsync(string url, string? folder = null) =>
        await _owner.PostAsJsonAsync("api/downloads", new NestDownloadRequest(url, folder), PairnetsJson.Options);

    private async Task<NestDownload> StartAsync(string url, string? folder = null)
    {
        var resp = await PostAsync(url, folder);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<NestDownload>(PairnetsJson.Options))!;
    }

    private async Task<NestDownloadList> ListAsync() =>
        (await _owner.GetFromJsonAsync<NestDownloadList>("api/downloads", PairnetsJson.Options))!;

    private async Task<NestDownload> WaitAsync(string id, Func<NestDownload, bool> until, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var d = (await ListAsync()).Downloads.FirstOrDefault(x => x.Id == id);
            if (d is not null && until(d))
                return d;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}: {d?.State} {d?.Error} {d?.Bytes}");
            await Task.Delay(50);
        }
    }

    private Task<NestDownload> WaitEndAsync(string id) => WaitAsync(id, d => !d.IsBusy, "the download to end");

    private static async Task<string?> Code(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))?.Code;

    private async Task<byte[]?> NestFileAsync(string path)
    {
        var opened = await _server.Store.OpenReadAsync(path, CancellationToken.None);
        if (opened is null)
            return null;
        await using (opened.Stream)
        {
            var copy = new MemoryStream();
            await opened.Stream.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [Fact]
    public async Task DownloadsALinkIntoDownloadsUnderAFreeName()
    {
        var first = await StartAsync(_site.Url("files/a.bin").ToString());
        Assert.Equal("127.0.0.1", first.Source); // the website's name, never the link
        Assert.Equal("Downloads", first.Folder);
        var done = await WaitEndAsync(first.Id);
        Assert.Equal(NestDownload.Done, done.State);
        Assert.Equal("Downloads/a.bin", done.Path);
        Assert.Equal(300_000, done.Total);
        Assert.False(done.Plain);
        Assert.Equal(_site.Files["a.bin"], await NestFileAsync("Downloads/a.bin"));

        // The same again: never overwritten, it gets the next free name.
        var second = await WaitEndAsync((await StartAsync(_site.Url("files/a.bin").ToString())).Id);
        Assert.Equal("Downloads/a (2).bin", second.Path);
        Assert.Equal(_site.Files["a.bin"], await NestFileAsync("Downloads/a (2).bin"));

        // The top of the nest, and the name the website gives.
        var named = await WaitEndAsync((await StartAsync(_site.Url("named").ToString(), "")).Id);
        Assert.Equal("Holiday video.mp4", named.Path);

        // The link itself is nowhere in the log or the list the apps read.
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains("/files/a.bin", StringComparison.Ordinal));
        Assert.DoesNotContain("files/a.bin", await _owner.GetStringAsync("api/downloads"));
        // The half-done files are gone.
        Assert.Empty(Directory.EnumerateFiles(_server.Paths.Tmp, "dl-*"));
    }

    [Theory]
    [InlineData("ftp://example.com/a.iso")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a link")]
    [InlineData("")]
    public async Task OnlyWebLinks(string url)
    {
        var resp = await PostAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(NestDownloadErrors.NotWeb, await Code(resp));
        Assert.Empty((await ListAsync()).Downloads);
    }

    [Theory]
    [InlineData("http://127.0.0.1:{nest}/api/health")]
    [InlineData("http://localhost:{nest}/api/health")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://[::1]:{nest}/")]
    [InlineData("http://[fd00::1]/")]
    public async Task ALinkIntoTheNestsOwnNetworkIsRefused(string url)
    {
        var resp = await PostAsync(url.Replace("{nest}", _server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(NestDownloadErrors.Blocked, await Code(resp));
    }

    [Fact]
    public async Task ARedirectIntoTheNestsOwnNetworkStops()
    {
        // The website sends the nest to the nest itself: the address check runs on every hop.
        var d = await WaitEndAsync((await StartAsync(_site.Url($"to/{_server.Port}").ToString())).Id);
        Assert.Equal(NestDownload.Failed, d.State);
        Assert.Equal(NestDownloadErrors.Blocked, d.Error);
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("100.64.0.1", true)] // check-secrets: allow (edge of the blocked 100.64.0.0/10 range)
    [InlineData("100.127.255.255", true)] // check-secrets: allow (edge of the blocked 100.64.0.0/10 range)
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("64:ff9b::a9fe:a9fe", true)] // 169.254.169.254 through NAT64
    [InlineData("2002:c0a8:0101::1", true)] // 192.168.1.1 through 6to4
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    [InlineData("64:ff9b::808:808", false)]
    public void TheAddressCheck(string address, bool blocked) =>
        Assert.Equal(blocked, SafeHttp.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public async Task RedirectsAreFollowedUpToTen()
    {
        var three = await WaitEndAsync((await StartAsync(_site.Url("redirect/3").ToString())).Id);
        Assert.Equal(NestDownload.Done, three.State);
        Assert.Equal("Downloads/a.bin", three.Path);

        var tooMany = await WaitEndAsync((await StartAsync(_site.Url("redirect/12").ToString())).Id);
        Assert.Equal(NestDownload.Failed, tooMany.State);
        Assert.Equal(NestDownloadErrors.Redirects, tooMany.Error);
    }

    [Fact]
    public async Task AWebsiteErrorSaysWhyAndTryAgainWorks()
    {
        var missing = await WaitEndAsync((await StartAsync(_site.Url("files/later.bin").ToString())).Id);
        Assert.Equal(NestDownload.Failed, missing.State);
        Assert.Equal(NestDownloadErrors.NotFound, missing.Error);
        Assert.Equal("the website said the file doesn't exist", NestDownloadErrors.Text(missing.Error));

        var refused = await WaitEndAsync((await StartAsync(_site.Url("forbidden").ToString())).Id);
        Assert.Equal(NestDownloadErrors.Refused, refused.Error);

        _site.Files["later.bin"] = Bytes(1000, 2);
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.PostAsync($"api/downloads/{missing.Id}/retry", null)).StatusCode);
        var again = await WaitEndAsync(missing.Id);
        Assert.Equal(NestDownload.Done, again.State);
        Assert.Equal(_site.Files["later.bin"], await NestFileAsync("Downloads/later.bin"));

        // Remove takes a stopped one off the list; Clear a finished one (its file stays).
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"api/downloads/{refused.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"api/downloads/{again.Id}")).StatusCode);
        Assert.Empty((await ListAsync()).Downloads);
        Assert.NotNull(await NestFileAsync("Downloads/later.bin"));
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.DeleteAsync($"api/downloads/{again.Id}")).StatusCode);
    }

    [Fact]
    public async Task KeepsFiveGigabytesFree()
    {
        // Announced too big: refused before it starts.
        Downloader.FreeSpace = () => NestDownloadRules.KeepFree + 1000;
        var tooBig = await WaitEndAsync((await StartAsync(_site.Url("files/a.bin").ToString())).Id);
        Assert.Equal(NestDownload.Failed, tooBig.State);
        Assert.Equal(NestDownloadErrors.NoSpace, tooBig.Error);
        Assert.Equal("there isn't enough free space on the nest", NestDownloadErrors.Text(tooBig.Error));

        // Of unknown size: stops at the line, and the half-done file goes.
        Downloader.FreeSpace = () => NestDownloadRules.KeepFree - 1;
        var unknown = await WaitEndAsync((await StartAsync(_site.Url("unknown/a.bin").ToString())).Id);
        Assert.Equal(NestDownloadErrors.NoSpace, unknown.Error);
        Assert.Null(await NestFileAsync("Downloads/a.bin"));
        Assert.False(File.Exists(Downloader.PartPath(unknown.Id)));

        Downloader.FreeSpace = () => NestDownloadRules.KeepFree * 4;
        var fits = await WaitEndAsync((await StartAsync(_site.Url("unknown/a.bin").ToString())).Id);
        Assert.Equal(NestDownload.Done, fits.State);
        Assert.Equal(_site.Files["a.bin"], await NestFileAsync("Downloads/a.bin"));
    }

    [Fact]
    public async Task ADroppedConnectionCarriesOnWhereItStopped()
    {
        Downloader.RetryDelay = TimeSpan.FromMilliseconds(100);
        var d = await WaitEndAsync((await StartAsync(_site.Url("cut/a.bin").ToString())).Id);
        Assert.Equal(NestDownload.Done, d.State);
        Assert.Equal(_site.Files["a.bin"], await NestFileAsync("Downloads/a.bin"));
        // The second request asked only for what was missing.
        Assert.Contains(_site.Ranges, r => r.StartsWith("bytes=", StringComparison.Ordinal) && !r.StartsWith("bytes=0-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANestRestartCarriesOnWhereItStopped()
    {
        _site.Files["big.bin"] = Bytes(4_000_000, 3); // about 3 seconds on the slow path
        var id = (await StartAsync(_site.Url("slow/big.bin").ToString())).Id;
        await WaitAsync(id, d => d.Bytes > 500_000, "the download to get going");

        await _server.RestartAsync();
        _owner.Dispose();
        _owner = _server.RawHttp(_server.Token);

        var d = await WaitEndAsync(id);
        Assert.Equal(NestDownload.Done, d.State);
        Assert.Equal(_site.Files["big.bin"], await NestFileAsync("Downloads/big.bin"));
        Assert.Contains(_site.Ranges, r => r.StartsWith("bytes=", StringComparison.Ordinal) && !r.StartsWith("bytes=0-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelStopsAndDeletesTheHalfDoneFile()
    {
        _site.Files["big.bin"] = Bytes(4_000_000, 4);
        var id = (await StartAsync(_site.Url("slow/big.bin").ToString())).Id;
        await WaitAsync(id, d => d.Bytes > 200_000, "the download to get going");
        Assert.True(File.Exists(Downloader.PartPath(id)));

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"api/downloads/{id}")).StatusCode);
        Assert.Empty((await ListAsync()).Downloads);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (File.Exists(Downloader.PartPath(id)) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.False(File.Exists(Downloader.PartPath(id)));
        Assert.Null(await NestFileAsync("Downloads/big.bin"));
    }

    [Fact]
    public async Task TwoAtOnceTheRestWait()
    {
        _site.Files["big.bin"] = Bytes(2_000_000, 5);
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
            ids.Add((await StartAsync(_site.Url("slow/big.bin").ToString())).Id);
        await WaitAsync(ids[0], d => d.State == NestDownload.Running && d.Bytes > 0, "the first to run");
        var list = await ListAsync();
        Assert.Equal(2, list.AtOnce);
        Assert.True(list.Downloads.Count(d => d.State == NestDownload.Running) <= 2);
        Assert.Contains(list.Downloads, d => d.State == NestDownload.Queued);
        foreach (var id in ids)
            Assert.Equal(NestDownload.Done, (await WaitEndAsync(id)).State);
        Assert.NotNull(await NestFileAsync("Downloads/big (3).bin"));
    }

    [Fact]
    public async Task AFolderThatIsAFileIsRefused()
    {
        Assert.Equal(System.Net.HttpStatusCode.OK, (await _owner.PutAsync("api/file?path=Taxes&base=none", new ByteArrayContent([1]))).StatusCode);
        var resp = await PostAsync(_site.Url("files/a.bin").ToString(), "Taxes/2027");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(NestDownloadErrors.BadFolder, await Code(resp));
        Assert.Equal(NestDownloadErrors.BadFolder, await Code(await PostAsync(_site.Url("files/a.bin").ToString(), "../up")));
    }

    [Fact]
    public async Task TheApiClientAndAnOlderNest()
    {
        using var api = _server.Client();
        var (started, error) = await api.StartDownloadAsync(_site.Url("files/a.bin").ToString(), "Films", CancellationToken.None);
        Assert.Null(error);
        Assert.Equal("Films", started!.Folder);
        var (_, refused) = await api.StartDownloadAsync("ftp://example.com/a", "", CancellationToken.None);
        Assert.Equal(NestDownloadErrors.NotWeb, refused);
        Assert.Contains((await api.GetDownloadsAsync(CancellationToken.None))!.Downloads, d => d.Id == started.Id);
        await WaitEndAsync(started.Id);
        Assert.True(await api.CancelDownloadAsync(started.Id, CancellationToken.None));
        Assert.False(await api.CancelDownloadAsync(started.Id, CancellationToken.None));
        Assert.False(await api.RetryDownloadAsync("nope", CancellationToken.None));

        // A nest from before nest downloads answers 404: the app says to update the nest.
        await using var old = await FakeWebsite.StartAsync();
        using var oldApi = new Pairnets.Core.Api.PairnetsApiClient(old.Url(""), "token", "test");
        Assert.Null(await oldApi.GetDownloadsAsync(CancellationToken.None));
        Assert.Equal(NestDownloadErrors.UpdateNest, (await oldApi.StartDownloadAsync("https://example.com/a", "", CancellationToken.None)).Error);
    }

    [Fact]
    public async Task OnAnEncryptedNestTheFileIsHeldNotEncrypted()
    {
        using var keys = ComputerKeys.Create();
        var ring = KeyRing.Create(_server.Store.ServerId, keys.AsTrusted("PC-1"));
        using (var http = _server.RawHttp(_server.Token))
        {
            http.DefaultRequestHeaders.Add(PairnetsHeaders.Features, PairnetsHeaders.SealedFeature);
            Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsync("api/keys/recovery",
                new ByteArrayContent(Envelope.SealForRecovery(ring, RecoveryKey.Create())))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsync($"api/keys/envelopes/{keys.Id}",
                new ByteArrayContent(Envelope.SealForComputer(ring, keys.AgreementPublicKey, keys)))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("api/keys/seal", null)).StatusCode);
        }
        Assert.True((await ListAsync()).Sealed);
        var version = _server.Store.CurrentVersion;

        var d = await WaitEndAsync((await StartAsync(_site.Url("files/a.bin").ToString())).Id);
        Assert.Equal(NestDownload.Done, d.State);
        Assert.True(d.Plain);
        Assert.Null(d.Path);
        // Nothing unencrypted among the encrypted files: it waits in the holding spot.
        Assert.Equal(version, _server.Store.CurrentVersion);
        Assert.Equal(_site.Files["a.bin"], await _owner.GetByteArrayAsync($"api/downloads/{d.Id}/file"));

        // Remove deletes it from the holding spot.
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"api/downloads/{d.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync($"api/downloads/{d.Id}/file")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Downloader.HeldDir, d.Id)));
    }

    [Fact]
    public async Task TheAppShowsTheDownloadsAndTellsOnceWhenOneIsDone()
    {
        using var folder = new TempDir("dl-session");
        using var state = new TempDir("dl-session-state");
        ClientSession Start() => ClientSession.Start(new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused-by-session",
            Folder = folder.Path,
            DeviceName = "mac",
            FirstRunCompleted = true,
        }, _server.Token, new PermanentDeleteTrash(), stateBaseDir: state.Path, runnerOptions: o => new RunnerOptions
        {
            ServerUrl = o.ServerUrl,
            Token = o.Token,
            DeviceId = o.DeviceId,
            PeriodicInterval = TimeSpan.FromHours(1),
        });

        var told = new List<NestDownload>();
        await using (var session = Start())
        {
            session.DownloadFinished += d =>
            {
                lock (told)
                    told.Add(d);
            };
            await session.RefreshDownloadsAsync();
            Assert.NotNull(session.Status.NestDownloads);
            Assert.False(session.Status.NestDownloadsUnsupported);
            Assert.Null(await session.StartDownloadAsync(_site.Url("files/a.bin").ToString(), "Films"));
            Assert.Equal("Only http:// and https:// links can be downloaded.", await session.StartDownloadAsync("ftp://example.com/a", ""));
            Assert.Single(session.Status.NestDownloads!.Downloads);

            // While one is busy the list is read every second, so the note comes without waiting for the 30 s refresh.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (told.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Single(told);
            Assert.Equal("Films/a.bin", told[0].Path);
            Assert.Contains(session.Activity.Items, i => i.Text == "a.bin finished downloading on the nest, in Films.");
            await session.RefreshDownloadsAsync();
            Assert.Single(told);
        }

        // The next start of the app doesn't tell it again.
        await using (var again = Start())
        {
            again.DownloadFinished += d =>
            {
                lock (told)
                    told.Add(d);
            };
            await again.RefreshDownloadsAsync();
            Assert.Single(told);
        }
    }

    [Fact]
    public void FileNamesAreMadeSafe()
    {
        Assert.Equal("ubuntu (2).iso", NestDownloader.Numbered("ubuntu.iso", 2));
        Assert.Equal("README (3)", NestDownloader.Numbered("README", 3));
        Assert.Equal("a_b_c.txt", NestDownloader.CleanName("a<b>c.txt"));
        Assert.Equal("_CON.txt", NestDownloader.CleanName("CON.txt"));
        Assert.Null(NestDownloader.CleanName("..."));
        Assert.Equal(200, NestDownloader.CleanName(new string('x', 300) + ".iso")!.Length);
        Assert.EndsWith(".iso", NestDownloader.CleanName(new string('x', 300) + ".iso"));
        Assert.Equal("download", NestDownloader.NameFromUrl(new Uri("https://example.com/")));
        Assert.Equal("my file.iso", NestDownloader.NameFromUrl(new Uri("https://example.com/a/my%20file.iso?token=secret")));
        Assert.Equal("Downloads", NestDownloader.CleanFolder(null));
        Assert.Equal("", NestDownloader.CleanFolder("/"));
        Assert.Null(NestDownloader.CleanFolder("a/../b"));
        Assert.Equal("Only http:// and https:// links can be downloaded.", NestDownloadErrors.Sentence(NestDownloadErrors.NotWeb));
    }
}
