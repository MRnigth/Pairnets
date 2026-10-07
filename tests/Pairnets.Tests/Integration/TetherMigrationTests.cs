using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Core.Legacy;
using Pairnets.Core.Logging;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Pairnets used to be called Tether. Installs, synced folders and apps from then must keep working:
/// these tests pin the old names and check that everything is taken over.
/// </summary>
public class TetherMigrationTests
{
    [Fact]
    public void TheOldNamesStayExactlyAsTetherWroteThem()
    {
        // Changing any of these strands existing users: their folders, tokens or apps stop being recognised.
        Assert.Equal("Tether", TetherNames.AppFolder);
        Assert.Equal(".tether-marker", TetherNames.MarkerFileName);
        Assert.Equal(".tether-tmp", TetherNames.TempFolderName);
        Assert.Equal("X-Tether-Server-Id", TetherNames.ServerIdHeader);
        Assert.Equal("X-Tether-Version", TetherNames.VersionHeader);
        Assert.Equal("X-Tether-Hash", TetherNames.HashHeader);
        Assert.Equal("X-Tether-Modified-Ms", TetherNames.ModifiedMsHeader);
        Assert.Equal("X-Tether-Client", TetherNames.ClientHeader);
        Assert.Equal("Pairnets.Client.Token.v1", TetherNames.DpapiEntropy);
        Assert.Equal(@"Local\Pairnets.Client.SingleInstance", TetherNames.WindowsMutex);
        Assert.Equal("Tether", TetherNames.KeychainService);
        Assert.Equal("tether", TetherNames.SecretToolService);
        Assert.Equal("Tether", TetherNames.WindowsRunValue);
        Assert.Equal("app.tether.client.plist", TetherNames.MacLaunchAgentFile);
        Assert.Equal("tether.desktop", TetherNames.LinuxAutostartFile);
    }

    [Fact]
    public void TheAppFoldersMoveOverWithEverythingInThem()
    {
        using var dir = new TempDir("migrate");
        var roaming = Directory.CreateDirectory(Path.Combine(dir.Path, "roaming")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(dir.Path, "local")).FullName;
        Directory.CreateDirectory(Path.Combine(roaming, "Tether"));
        File.WriteAllText(Path.Combine(roaming, "Tether", "settings.json"), "{\"serverUrl\":\"http://192.0.2.10:5075/\"}");
        Directory.CreateDirectory(Path.Combine(local, "Tether", "abc123"));
        File.WriteAllText(Path.Combine(local, "Tether", "abc123", "state.db"), "state");

        var result = TetherMigration.MoveAppFolders([roaming, local], () => false);

        Assert.Equal(TetherMigration.FolderResult.Moved, result);
        Assert.False(Directory.Exists(Path.Combine(roaming, "Tether")));
        Assert.Contains("192.0.2.10", File.ReadAllText(Path.Combine(roaming, "Pairnets", "settings.json")));
        Assert.Equal("state", File.ReadAllText(Path.Combine(local, "Pairnets", "abc123", "state.db")));
        // A second start finds nothing more to do.
        Assert.Equal(TetherMigration.FolderResult.NothingToMove, TetherMigration.MoveAppFolders([roaming, local], () => false));
    }

    [Fact]
    public void NothingMovesWhileTetherIsRunningOrWhenPairnetsHasItsOwnData()
    {
        using var dir = new TempDir("migrate");
        Directory.CreateDirectory(Path.Combine(dir.Path, "Tether"));

        Assert.Equal(TetherMigration.FolderResult.OldAppRunning, TetherMigration.MoveAppFolders([dir.Path], () => true));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Tether")));

        Directory.CreateDirectory(Path.Combine(dir.Path, "Pairnets"));
        Assert.Equal(TetherMigration.FolderResult.NothingToMove, TetherMigration.MoveAppFolders([dir.Path], () => throw new InvalidOperationException("not asked")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Tether")));
    }

    [Fact]
    public void TheOldDesktopAppIsSeenRunningByItsLock()
    {
        using var dir = new TempDir("migrate");
        Assert.False(TetherMigration.OldDesktopAppRunning(dir.Path));
        var lockFile = Path.Combine(Directory.CreateDirectory(Path.Combine(dir.Path, "Tether")).FullName, "app.lock");
        using (new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.True(TetherMigration.OldDesktopAppRunning(dir.Path));
        Assert.False(TetherMigration.OldDesktopAppRunning(dir.Path));
    }

    [Fact]
    public void ASyncFolderKeepsItsIdentityAndLosesTethersLeftovers()
    {
        using var dir = new TempDir("folder");
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(dir.Path, TetherNames.MarkerFileName), id + Environment.NewLine);
        Directory.CreateDirectory(Path.Combine(dir.Path, TetherNames.TempFolderName));
        File.WriteAllText(Path.Combine(dir.Path, TetherNames.TempFolderName, "half-downloaded.part"), "x");

        Assert.Equal(id, StateLocator.ReadMarker(dir.Path));
        Assert.False(File.Exists(Path.Combine(dir.Path, TetherNames.MarkerFileName)));
        Assert.True(File.Exists(Path.Combine(dir.Path, PathRules.MarkerFileName)));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, TetherNames.TempFolderName)));
    }

    [Fact]
    public void TheOldNamesCanNeverBeSynced()
    {
        Assert.Equal(PathProblem.ReservedName, PathRules.Check(TetherNames.MarkerFileName));
        Assert.Equal(PathProblem.ReservedName, PathRules.Check(TetherNames.TempFolderName + "/x.bin"));
        Assert.Equal(PathProblem.ReservedName, PathRules.Check("docs/" + TetherNames.TempFolderName + "/x.bin"));
        var ignore = new IgnoreList([]);
        Assert.True(ignore.IsIgnored(TetherNames.MarkerFileName));
        Assert.True(ignore.IsIgnoredDirectory(TetherNames.TempFolderName));
    }

    [Fact]
    public async Task AFolderSyncedAsTetherSyncsOnWithoutBeingBlocked()
    {
        await using var server = await TestServer.StartAsync();
        await using var pc = new Device("pc", server);
        File.WriteAllText(Path.Combine(pc.Folder, "a.txt"), "a");
        File.SetLastWriteTimeUtc(Path.Combine(pc.Folder, "a.txt"), DateTime.UtcNow.AddMinutes(-5));
        await pc.SyncUntilQuietAsync();
        // As Tether left it: the same folder id under the old marker name.
        File.Move(Path.Combine(pc.Folder, PathRules.MarkerFileName), Path.Combine(pc.Folder, TetherNames.MarkerFileName));

        File.WriteAllText(Path.Combine(pc.Folder, "b.txt"), "b");
        File.SetLastWriteTimeUtc(Path.Combine(pc.Folder, "b.txt"), DateTime.UtcNow.AddMinutes(-5));
        var result = await pc.SyncAsync();

        Assert.Equal(PassOutcome.Completed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(pc.Folder, PathRules.MarkerFileName)));
        Assert.False(File.Exists(Path.Combine(pc.Folder, TetherNames.MarkerFileName)));
        Assert.Contains(server.Store.ReadManifest(null).Entries, e => e.Path == "b.txt");
    }

    [Fact]
    public async Task TheServerAlsoSpeaksToTetherApps()
    {
        await using var server = await TestServer.StartAsync();
        using var http = server.RawHttp(server.Token);
        await http.PutAsync("api/file?path=a.txt&base=none&mtime=1700000000000", new StringContent("a"));

        var manifest = await http.GetAsync("api/manifest");
        Assert.Equal(manifest.Headers.GetValues(PairnetsHeaders.ServerId), manifest.Headers.GetValues(TetherNames.ServerIdHeader));
        Assert.Equal(manifest.Headers.GetValues(PairnetsHeaders.Version), manifest.Headers.GetValues(TetherNames.VersionHeader));
        var file = await http.GetAsync("api/file?path=a.txt");
        Assert.Equal(ContentHash.Of("a"u8), file.Headers.GetValues(TetherNames.HashHeader).Single());
        Assert.Equal("1700000000000", file.Headers.GetValues(TetherNames.ModifiedMsHeader).Single());

        // A Tether app announces itself with the old header; the Devices list still shows its version.
        using var tether = server.RawHttp(server.Token);
        tether.DefaultRequestHeaders.Add("X-Device-Id", "old-laptop");
        tether.DefaultRequestHeaders.Add(TetherNames.ClientHeader, "1.0.58; Windows");
        await tether.GetAsync("api/info");
        var devices = (await server.Client().GetDevicesAsync(default))!;
        Assert.Equal("1.0.58", devices.Single(d => d.Name == "old-laptop").AppVersion);
    }

    /// <summary>Plays a Tether server: it answers with the old header names only.</summary>
    private sealed class TetherServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            resp.Headers.Add(TetherNames.ServerIdHeader, "old-server");
            resp.Headers.Add(TetherNames.VersionHeader, "42");
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task PairnetsAppsStillSyncWithATetherServer()
    {
        using var api = new PairnetsApiClient(new Uri("http://127.0.0.1:5075/"), "a-token-a-token-a-token", "pc", new TetherServer());
        var manifest = await api.GetManifestAsync(null, default);
        Assert.Equal("old-server", manifest.ServerId);
        Assert.Equal(42, manifest.Version);
    }

    [Fact]
    public void OldTetherLogsAgeOutToo()
    {
        using var dir = new TempDir("logs");
        var clock = new ManualClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        File.WriteAllText(Path.Combine(dir.Path, "tether-20260101.log"), "old");
        File.WriteAllText(Path.Combine(dir.Path, "tether-20260225.log"), "recent");
        using (var provider = new RollingFileLoggerProvider(dir.Path, 14, LogLevel.Information, clock))
            provider.CreateLogger("Test").LogInformation("hello");
        var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray();
        Assert.DoesNotContain("tether-20260101.log", files);
        Assert.Contains("tether-20260225.log", files);
    }
}
