using System.Net;
using System.Text;
using Pairnets.Core.Client;
using Pairnets.Core.Hashing;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

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

    /// <summary>A release as the workflow publishes it: SHA256SUMS.txt lists the installer and version.json, signed with the test key.</summary>
    private static Dictionary<string, byte[]> Release(string version, byte[]? setup = null, bool signed = true)
    {
        var versionJson = Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"commit\":\"abc\"}}");
        var sums = Encoding.UTF8.GetBytes($"{ContentHash.Of(Setup)}  PairnetsSetup.exe\n{ContentHash.Of([1])}  other.bin\n{ContentHash.Of(versionJson)}  version.json\n");
        var files = new Dictionary<string, byte[]>
        {
            ["version.json"] = versionJson,
            ["SHA256SUMS.txt"] = sums,
            ["PairnetsSetup.exe"] = setup ?? Setup,
        };
        if (signed)
            files["SHA256SUMS.txt.sig"] = TestReleaseKey.Sign(sums);
        return files;
    }

    private static UpdateChecker Checker(Dictionary<string, byte[]> files, string? key = null) =>
        new(new HttpClient(new FakeGitHub(files)), Base, signingKeyPem: key ?? TestReleaseKey.PublicKeyPem);

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
        var info = await Checker(Release("1.0.58")).CheckAsync("1.0.52", "PairnetsSetup.exe", default);
        Assert.NotNull(info);
        Assert.Equal("1.0.58", info!.Version);
        Assert.Equal(new Uri(Base, "PairnetsSetup.exe"), info.AssetUrl);
        Assert.Equal(ContentHash.Of(Setup), info.Sha256);
        Assert.True(info.Signed);
    }

    [Fact]
    public async Task AReleaseWithoutAGoodSignatureIsFoundButNeverDownloaded()
    {
        using var dir = new TempDir("upd");
        var dest = Path.Combine(dir.Path, "PairnetsSetup.exe");
        var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfoPem();
        var garbled = Release("1.0.58");
        garbled["SHA256SUMS.txt.sig"] = "not a signature"u8.ToArray();
        var edited = Release("1.0.58");
        edited["SHA256SUMS.txt"] = [.. edited["SHA256SUMS.txt"], .. "0000  extra.bin\n"u8.ToArray()]; // signed before the edit
        foreach (var checker in new[] { Checker(Release("1.0.58", signed: false)), Checker(garbled), Checker(edited), Checker(Release("1.0.58"), otherKey) })
        {
            var info = await checker.CheckAsync("1.0.52", "PairnetsSetup.exe", default);
            Assert.NotNull(info);
            Assert.False(info!.Signed);
            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() => checker.DownloadVerifiedAsync(info, dest, null, default));
            Assert.True(ex.NotSigned);
            Assert.Empty(Directory.EnumerateFiles(dir.Path));
        }
    }

    [Fact]
    public async Task AnOldSignedReleaseCannotPassForANewerVersion()
    {
        // version.json says 1.0.99, but the signed checksums are those of the 1.0.58 release: its version.json differs.
        var old = Release("1.0.58");
        old["version.json"] = Encoding.UTF8.GetBytes("{\"version\":\"1.0.99\",\"commit\":\"abc\"}");
        var info = await Checker(old).CheckAsync("1.0.60", "PairnetsSetup.exe", default);
        Assert.NotNull(info);
        Assert.False(info!.Signed);
    }

    [Fact]
    public void ThePairnetsKeyIsBuiltInAndIsAP256Key()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(ReleaseSigning.PublicKeyPem);
        Assert.Equal(256, key.KeySize);
        Assert.Equal(File.ReadAllText(Path.Combine(RepoPaths.Root, "deploy", "release-signing.pub")).ReplaceLineEndings("\n"),
            ReleaseSigning.PublicKeyPem.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("PRIVATE", ReleaseSigning.PublicKeyPem);
        // Something signed by another key does not pass as Pairnets'.
        Assert.False(ReleaseSigning.Verify("x"u8, TestReleaseKey.Sign("x"u8.ToArray()), ReleaseSigning.PublicKeyPem));
        Assert.True(ReleaseSigning.Verify("x"u8, TestReleaseKey.Sign("x"u8.ToArray()), TestReleaseKey.PublicKeyPem));
        Assert.False(ReleaseSigning.Verify("x"u8, [], TestReleaseKey.PublicKeyPem));
        Assert.False(ReleaseSigning.Verify("x"u8, TestReleaseKey.Sign("x"u8.ToArray()), "garbage"));
    }

    [Fact]
    public async Task NothingWhenCurrentMissingGarbledOrNoAsset()
    {
        Assert.Null(await Checker(Release("1.0.52")).CheckAsync("1.0.52", "PairnetsSetup.exe", default));
        Assert.Null(await Checker([]).CheckAsync("1.0.52", "PairnetsSetup.exe", default)); // private repo / no release
        Assert.Null(await Checker(new() { ["version.json"] = "not json"u8.ToArray() }).CheckAsync("1.0.52", "PairnetsSetup.exe", default));
        Assert.Null(await Checker(Release("1.0.58")).CheckAsync("1.0.52", "Pairnets-macos-arm64.dmg", default)); // not in SHA256SUMS
        Assert.Null(await Checker(Release("1.0.58")).CheckAsync("1.0.52", null, default));
    }

    [Fact]
    public async Task DownloadIsVerified()
    {
        using var dir = new TempDir("upd");
        var good = Release("1.0.58");
        var info = (await Checker(good).CheckAsync("1.0.52", "PairnetsSetup.exe", default))!;
        var dest = Path.Combine(dir.Path, "PairnetsSetup.exe");
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
        using var service = new UpdateService(Checker(Release("1.0.58")), "1.0.52", "PairnetsSetup.exe");
        var announced = new List<UpdateInfo>();
        service.UpdateAvailable += announced.Add;
        await service.CheckNowAsync(default);
        await service.CheckNowAsync(default);
        Assert.Equal("1.0.58", service.Available!.Version);
        Assert.Single(announced); // once per version, not on every check
        Assert.NotNull(service.LastChecked);
    }
}
