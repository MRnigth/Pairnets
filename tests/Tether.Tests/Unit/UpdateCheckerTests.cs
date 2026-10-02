using System.Net;
using System.Text;
using Tether.Core.Client;
using Tether.Core.Hashing;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Unit;

public class UpdateCheckerTests
{
    private sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var name = request.RequestUri!.Segments[^1];
            return Task.FromResult(files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static readonly Uri Base = new("https://example.invalid/releases/latest/download/");
    private static readonly byte[] Setup = Encoding.UTF8.GetBytes("pretend installer");

    private static Dictionary<string, byte[]> Release(string version, byte[]? setup = null) => new()
    {
        ["version.json"] = Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"commit\":\"abc\"}}"),
        ["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes($"{ContentHash.Of(Setup)}  TetherSetup.exe\n{ContentHash.Of([1])}  other.bin\n"),
        ["TetherSetup.exe"] = setup ?? Setup,
    };

    private static UpdateChecker Checker(Dictionary<string, byte[]> files) => new(new HttpClient(new FakeGitHub(files)), Base);

    [Theory]
    [InlineData("1.0.58", "1.0.52", true)]
    [InlineData("1.0.52", "1.0.52", false)]
    [InlineData("1.0.40", "1.0.52", false)]
    [InlineData("1.1.0", "1.0.99", true)]
    [InlineData("v2.0", "1.9.9", true)]
    [InlineData("garbage", "1.0.0", false)]
    [InlineData(null, "1.0.0", false)]
    public void ComparesVersions(string? candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void ServerIsOlderOnlyWhenItSaysSo()
    {
        Assert.True(UpdateChecker.ServerIsOlder("1.0.50", "1.0.58"));
        Assert.False(UpdateChecker.ServerIsOlder("1.0.58", "1.0.58"));
        Assert.False(UpdateChecker.ServerIsOlder("1.0.60", "1.0.58"));
        Assert.False(UpdateChecker.ServerIsOlder(null, "1.0.58")); // old servers report no version
    }

    [Fact]
    public async Task FindsANewerReleaseWithItsChecksum()
    {
        var info = await Checker(Release("1.0.58")).CheckAsync("1.0.52", "TetherSetup.exe", default);
        Assert.NotNull(info);
        Assert.Equal("1.0.58", info!.Version);
        Assert.Equal(new Uri(Base, "TetherSetup.exe"), info.AssetUrl);
        Assert.Equal(ContentHash.Of(Setup), info.Sha256);
    }

    [Fact]
    public async Task NothingWhenCurrentMissingGarbledOrNoAsset()
    {
        Assert.Null(await Checker(Release("1.0.52")).CheckAsync("1.0.52", "TetherSetup.exe", default));
        Assert.Null(await Checker([]).CheckAsync("1.0.52", "TetherSetup.exe", default)); // private repo / no release
        Assert.Null(await Checker(new() { ["version.json"] = "not json"u8.ToArray() }).CheckAsync("1.0.52", "TetherSetup.exe", default));
        Assert.Null(await Checker(Release("1.0.58")).CheckAsync("1.0.52", "Tether-macos-arm64.dmg", default)); // not in SHA256SUMS
        Assert.Null(await Checker(Release("1.0.58")).CheckAsync("1.0.52", null, default));
    }

    [Fact]
    public async Task DownloadIsVerified()
    {
        using var dir = new TempDir("upd");
        var good = Release("1.0.58");
        var info = (await Checker(good).CheckAsync("1.0.52", "TetherSetup.exe", default))!;
        var dest = Path.Combine(dir.Path, "TetherSetup.exe");
        await Checker(good).DownloadVerifiedAsync(info, dest, null, default);
        Assert.Equal(Setup, File.ReadAllBytes(dest));

        File.Delete(dest);
        var tampered = Release("1.0.58", "evil installer"u8.ToArray());
        await Assert.ThrowsAsync<UpdateVerificationException>(() => Checker(tampered).DownloadVerifiedAsync(info, dest, null, default));
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Fact]
    public async Task ServiceRemembersWhatItFound()
    {
        using var service = new UpdateService(Checker(Release("1.0.58")), "1.0.52", "TetherSetup.exe");
        var announced = new List<UpdateInfo>();
        service.UpdateAvailable += announced.Add;
        await service.CheckNowAsync(default);
        await service.CheckNowAsync(default);
        Assert.Equal("1.0.58", service.Available!.Version);
        Assert.Single(announced); // once per version, not on every check
        Assert.NotNull(service.LastChecked);
    }
}
