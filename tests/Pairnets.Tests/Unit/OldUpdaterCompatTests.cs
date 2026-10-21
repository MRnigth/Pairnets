using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// Apps that are already out there must keep finding and accepting new releases: they only know the file names and the
/// layout of the release they were built from. An installed 1.0.52 (from before signed releases) reads version.json and
/// SHA256SUMS.txt from the latest release, downloads PairnetsSetup.exe and checks it against the checksum, nothing more;
/// the server's update.sh reads SHA256SUMS.txt and pairnets-server-linux-x64.tar.gz (UpdateScriptTests covers it). These
/// tests hold the release to that: a frozen copy of the 1.0.52 updater updates from a release laid out as the release
/// workflow lays it out today, and the workflow still publishes every file an older app or server asks for.
/// </summary>
public class OldUpdaterCompatTests
{
    private static readonly Uri Base = new("https://example.invalid/releases/latest/download/");

    /// <summary>Every file the "Latest build" release has, by the names the workflow gives them (contents are stand-ins).</summary>
    private static readonly string[] ReleaseFiles =
    [
        "get.sh", "pairnets-client-win-x64.zip", "pairnets-desktop-linux-x64.tar.gz", "Pairnets-macos-arm64.dmg", "Pairnets-macos-arm64.zip",
        "Pairnets-macos-x64.dmg", "Pairnets-macos-x64.zip", "pairnets-server-linux-x64.tar.gz", "PairnetsSetup.exe",
    ];

    /// <summary>
    /// The release folder as .github/workflows/release.yml writes it: version.json by its printf, SHA256SUMS.txt by
    /// <c>find . -maxdepth 1 -type f ! -name 'SHA256SUMS.txt*' -printf '%P\0' | sort -z | xargs -0 sha256sum</c> (every
    /// file but the checksums, sorted by name, "hash  name" lines), and SHA256SUMS.txt.sig by scripts/sign-release.sh.
    /// </summary>
    private static Dictionary<string, byte[]> ReleaseAsTheWorkflowWritesIt(string version)
    {
        var files = ReleaseFiles.ToDictionary(n => n, n => n switch
        {
            "pairnets-client-win-x64.zip" => TestUpdatePackages.Make(n, Encoding.UTF8.GetBytes("new Pairnets.exe " + version)),
            "pairnets-desktop-linux-x64.tar.gz" => TestUpdatePackages.Make(n, Encoding.UTF8.GetBytes("new Pairnets " + version)),
            _ => Encoding.UTF8.GetBytes($"release file {n} of {version}\n"),
        });
        files["version.json"] = Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"commit\":\"{new string('a', 40)}\"}}\n");
        var sums = new StringBuilder();
        foreach (var name in files.Keys.Order(StringComparer.Ordinal))
            sums.Append(Convert.ToHexString(SHA256.HashData(files[name])).ToLowerInvariant()).Append("  ").Append(name).Append('\n');
        files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(sums.ToString());
        files["SHA256SUMS.txt.sig"] = TestReleaseKey.Sign(files["SHA256SUMS.txt"]);
        return files;
    }

    private sealed class FakeGitHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = request.RequestUri!.Segments[^1];
            lock (Asked)
                Asked.Add(name);
            return Task.FromResult(files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task AnInstalled1_0_52_StillFindsDownloadsAndAcceptsTheCurrentRelease()
    {
        var release = ReleaseAsTheWorkflowWritesIt("1.0.60");
        var github = new FakeGitHub(release);
        var old = new Updater1_0_52(new HttpClient(github), Base);
        using var dir = new TempDir("old-updater");

        var found = await old.CheckAsync("1.0.52", "PairnetsSetup.exe", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("1.0.60", found.Version);
        var setup = dir.Combine("PairnetsSetup.exe");
        await old.DownloadVerifiedAsync(found, setup, CancellationToken.None);
        Assert.Equal(release["PairnetsSetup.exe"], await File.ReadAllBytesAsync(setup));
        Assert.Equal(["version.json", "SHA256SUMS.txt", "PairnetsSetup.exe"], github.Asked);
    }

    [Fact]
    public async Task TheFrozen1_0_52_UpdaterStillRefusesADamagedDownload()
    {
        var release = ReleaseAsTheWorkflowWritesIt("1.0.60");
        release["PairnetsSetup.exe"] = Encoding.UTF8.GetBytes("not the listed installer");
        var old = new Updater1_0_52(new HttpClient(new FakeGitHub(release)), Base);
        using var dir = new TempDir("old-updater");

        var found = await old.CheckAsync("1.0.52", "PairnetsSetup.exe", CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => old.DownloadVerifiedAsync(found!, dir.Combine("PairnetsSetup.exe"), CancellationToken.None));
        Assert.False(File.Exists(dir.Combine("PairnetsSetup.exe")));
    }

    [Theory]
    [InlineData(UpdateMethod.Installer, UpdatePackage.WindowsInstaller, null)]
    [InlineData(UpdateMethod.ReplaceApp, UpdatePackage.WindowsZip, "Pairnets.exe")]
    [InlineData(UpdateMethod.ReplaceApp, UpdatePackage.LinuxDesktop, "Pairnets")]
    [InlineData(UpdateMethod.ByHand, "Pairnets-macos-arm64.dmg", null)]
    [InlineData(UpdateMethod.ByHand, "Pairnets-macos-x64.dmg", null)]
    public async Task TodaysUpdater_FindsEveryPackage_InTheCurrentRelease(UpdateMethod method, string asset, string? appFile)
    {
        var release = ReleaseAsTheWorkflowWritesIt("1.0.60");
        var checker = new UpdateChecker(new HttpClient(new FakeGitHub(release)), Base, signingKeyPem: TestReleaseKey.PublicKeyPem);
        using var dir = new TempDir("updater");

        var found = await checker.CheckAsync("1.0.59", asset, CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found.Signed);
        var download = dir.Combine(asset);
        await checker.DownloadVerifiedAsync(found, download, null, CancellationToken.None);
        if (method != UpdateMethod.ReplaceApp)
            return;
        var app = dir.Combine(appFile!);
        await File.WriteAllTextAsync(app, "the old app");
        SelfReplace.Install(download, appFile!, app);
        Assert.StartsWith("new Pairnets", await File.ReadAllTextAsync(app));
    }

    [Fact]
    public void TheReleaseWorkflow_StillPublishesEveryFileOlderAppsAndServersAskFor()
    {
        var workflow = RepoPaths.Read(".github", "workflows", "release.yml");

        // Built under these names and copied into release-files, which goes up as the release's files.
        foreach (var built in new[] { "dist/pairnets-client-win-x64.zip", "dist/pairnets-desktop-linux-x64.tar.gz", "dist/pairnets-server-linux-x64.tar.gz" })
            Assert.Contains(built, workflow);
        Assert.Contains(@"/O$PWD\dist"" deploy\installer\pairnets.iss", workflow);
        Assert.Contains("OutputBaseFilename=PairnetsSetup\n", RepoPaths.Read("deploy", "installer", "pairnets.iss"));
        Assert.Contains("gh release create latest release-files/*", workflow);
        // The two files every older updater reads first, in the format they parse.
        Assert.Contains("printf '{\"version\":\"%s\",\"commit\":\"%s\"}\\n' \"$v\" \"$GITHUB_SHA\" > release-files/version.json", workflow);
        Assert.Contains("find . -maxdepth 1 -type f ! -name 'SHA256SUMS.txt*' -printf '%P\\0' | sort -z | xargs -0 sha256sum > SHA256SUMS.txt", workflow);
        Assert.Contains("bash scripts/sign-release.sh release-files", workflow);
        // Pairnets.exe stays at the top of the zip and Pairnets at the top of the Linux app's folder (what an update swaps).
        Assert.Contains("Compress-Archive -Path publish/client/Pairnets.exe", workflow);
        Assert.Matches(new Regex(@"tar -C dist -czf dist/pairnets-desktop-linux-x64\.tar\.gz pairnets-desktop-linux-x64"), workflow);
    }

    [Theory]
    [InlineData("1.0.59.1", "1.0.59", true)]   // the quick fix (tag v1.0.59.1) after 1.0.59
    [InlineData("1.0.60", "1.0.59.1", true)]   // the next normal release (1.0.<run number>) after it
    [InlineData("1.0.61", "1.0.59.1", true)]   // (a failed run can skip a number)
    [InlineData("1.0.59.1", "1.0.59.1", false)]
    [InlineData("1.0.59", "1.0.59.1", false)]
    [InlineData("1.0.59.2", "1.0.59.1", true)]
    [InlineData("1.0.59.10", "1.0.59.9", true)]
    public void QuickFixVersions_CompareAsNumbers_InTodaysAppsAndIn1_0_52(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));
        Assert.Equal(newer, Updater1_0_52.IsNewerForTests(candidate, current));
    }

    [Fact]
    public void AQuickFixApp_CallsItselfByAllFourNumbers()
    {
        // The release workflow builds a v1.0.59.1 tag with -p:Version=1.0.59.1, which becomes the assembly version.
        Assert.Equal("1.0.59.1", PairnetsInfo.FormatVersion(Version.Parse("1.0.59.1")));
        Assert.Equal("1.0.59", PairnetsInfo.FormatVersion(Version.Parse("1.0.59.0")));
        Assert.Equal("1.0.59", PairnetsInfo.FormatVersion(Version.Parse("1.0.59")));
        Assert.Equal("1.0.59", PairnetsInfo.FormatVersion(new Version(1, 0, 59)));
        // ...so its server, on 1.0.59.1 too, is up to date, and one on 1.0.59 is older.
        Assert.False(UpdateChecker.ServerIsOlder("1.0.59.1", "1.0.59.1"));
        Assert.True(UpdateChecker.ServerIsOlder("1.0.59", "1.0.59.1"));
        Assert.False(UpdateChecker.ServerIsOlder("1.0.60", "1.0.59.1"));
    }

    [Fact]
    public async Task AQuickFixRelease_ReachesOldApps_AndIsNotOfferedAgainOnceInstalled()
    {
        var release = ReleaseAsTheWorkflowWritesIt("1.0.59.1");

        var old = new Updater1_0_52(new HttpClient(new FakeGitHub(release)), Base);
        Assert.Equal("1.0.59.1", (await old.CheckAsync("1.0.59", "PairnetsSetup.exe", CancellationToken.None))?.Version);
        Assert.Equal("1.0.59.1", (await old.CheckAsync("1.0.52", "PairnetsSetup.exe", CancellationToken.None))?.Version);

        var checker = new UpdateChecker(new HttpClient(new FakeGitHub(release)), Base, signingKeyPem: TestReleaseKey.PublicKeyPem);
        Assert.Equal("1.0.59.1", (await checker.CheckAsync("1.0.59", UpdatePackage.WindowsZip, CancellationToken.None))?.Version);
        var installed = PairnetsInfo.FormatVersion(Version.Parse("1.0.59.1"));
        Assert.Null(await checker.CheckAsync(installed, UpdatePackage.WindowsInstaller, CancellationToken.None));

        var next = ReleaseAsTheWorkflowWritesIt("1.0.60");
        var later = new UpdateChecker(new HttpClient(new FakeGitHub(next)), Base, signingKeyPem: TestReleaseKey.PublicKeyPem);
        Assert.Equal("1.0.60", (await later.CheckAsync(installed, UpdatePackage.WindowsInstaller, CancellationToken.None))?.Version);
    }

    [Fact]
    public void ATaggedQuickFix_IsBuiltWithItsOwnNumber_AndBecomesTheLatestRelease()
    {
        var workflow = RepoPaths.Read(".github", "workflows", "release.yml");
        // v1.0.59.1 -> 1.0.59.1 for every package, the installer and version.json.
        Assert.Contains("if [[ \"$GITHUB_REF\" == refs/tags/v* ]]; then echo \"version=${GITHUB_REF_NAME#v}\"", workflow);
        Assert.Contains("if [[ \"$GITHUB_REF\" == refs/tags/v* ]]; then v=\"${GITHUB_REF_NAME#v}\"", workflow);
        // The apps and the server read GitHub's latest release, so the tagged one must be marked latest.
        Assert.Matches(new Regex(@"gh release create ""\$GITHUB_REF_NAME"" release-files/\* .*--latest"), workflow);
        // The server package and update.sh take only numbers and dots; four of them are fine.
        Assert.Matches(new Regex(@"^[0-9]+(\.[0-9]+)*$"), "1.0.59.1");
        Assert.Contains(@"[[ ""$VERSION"" =~ ^[0-9]+(\.[0-9]+)*$ ]]", RepoPaths.Read("scripts", "package-server.sh"));
    }

    /// <summary>
    /// The update check and download of Pairnets 1.0.52 (commit 8a4c1ab, src/Pairnets.Core/Client/UpdateChecker.cs), kept as
    /// it was: it knows nothing of SHA256SUMS.txt.sig and only checks the download against SHA256SUMS.txt. Do not change it
    /// to match today's code; it stands for the apps that are already installed.
    /// </summary>
    private sealed class Updater1_0_52(HttpClient http, Uri downloadBase)
    {
        public sealed record Found(string Version, string AssetName, Uri AssetUrl, string Sha256);

        private sealed record VersionFile(string Version, string? Commit);

        public async Task<Found?> CheckAsync(string currentVersion, string? asset, CancellationToken ct)
        {
            var file = await http.GetFromJsonAsync<VersionFile>(new Uri(downloadBase, "version.json"), PairnetsJson.Options, ct).ConfigureAwait(false);
            if (file is null || !IsNewer(file.Version, currentVersion) || asset is null)
                return null;
            var sums = await http.GetStringAsync(new Uri(downloadBase, "SHA256SUMS.txt"), ct).ConfigureAwait(false);
            var sha = FindChecksum(sums, asset);
            return sha is null ? null : new Found(file.Version.Trim(), asset, new Uri(downloadBase, asset), sha);
        }

        public async Task DownloadVerifiedAsync(Found update, string destination, CancellationToken ct)
        {
            var tmp = destination + ".part";
            try
            {
                var bytes = await http.GetByteArrayAsync(update.AssetUrl, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(hash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The download did not match its checksum. Nothing was installed.");
                File.Move(tmp, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
        }

        private static string? FindChecksum(string sums, string asset)
        {
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == asset && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                    return parts[0].ToLowerInvariant();
            }
            return null;
        }

        public static bool IsNewerForTests(string? candidate, string? current) => IsNewer(candidate, current);

        private static bool IsNewer(string? candidate, string? current) =>
            TryParse(candidate, out var c) && TryParse(current, out var cur) && c > cur;

        private static bool TryParse(string? text, out Version version)
        {
            version = new Version(0, 0);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var parts = text.Trim().TrimStart('v').Split('.');
            if (parts.Length is < 2 or > 4 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                return false;
            version = Version.Parse(string.Join('.', parts.Concat(Enumerable.Repeat("0", 4 - parts.Length))));
            return true;
        }
    }
}
