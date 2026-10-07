using Microsoft.Extensions.Logging;
using Pairnets.Core.Logging;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public class ClientSupportTests
{
    [Fact]
    public void SettingsRoundTripWithoutPlainToken()
    {
        using var dir = new TempDir("settings");
        var path = dir.Combine("sub", "settings.json");
        var protector = new ReversingProtector();
        var s = new ClientSettings
        {
            ServerUrl = "http://example.invalid:5075/",
            ProtectedToken = protector.Protect("super-secret-token-value"),
            Folder = dir.Path,
            DeviceName = "PC",
            ExtraIgnore = ["*.bak"],
            FirstRunCompleted = true,
        };
        SettingsStore.Save(path, s);
        Assert.DoesNotContain("super-secret-token-value", File.ReadAllText(path));
        var loaded = SettingsStore.Load(path);
        Assert.True(loaded.IsComplete);
        Assert.Equal("super-secret-token-value", protector.Unprotect(loaded.ProtectedToken!));
        Assert.Equal(["*.bak"], loaded.ExtraIgnore);

        File.WriteAllText(path, "{ not json");
        Assert.False(SettingsStore.Load(path).IsComplete);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.False(SettingsStore.Load(dir.Combine("missing.json")).IsComplete);
    }

    [Fact]
    public void StateDirIsStablePerFolder()
    {
        using var dir = new TempDir("statedir");
        var a = PairnetsPaths.StateDirFor(dir.Combine("Work"), dir.Path);
        Assert.Equal(a, PairnetsPaths.StateDirFor(dir.Combine("Work") + Path.DirectorySeparatorChar, dir.Path));
        Assert.NotEqual(a, PairnetsPaths.StateDirFor(dir.Combine("Other"), dir.Path));
        Assert.Equal(16, Path.GetFileName(a).Length);
    }

    [Fact]
    public void MovedFolderStateIsFoundByMarkerAndAdopted()
    {
        using var baseDir = new TempDir("local");
        using var oldFolder = new TempDir("old");
        using var newFolder = new TempDir("new");
        var oldState = PairnetsPaths.StateDirFor(oldFolder.Path, baseDir.Path);
        using (var db = new StateDb(Path.Combine(oldState, "state.db")))
        {
            db.MarkerId = "11111111-2222-3333-4444-555555555555";
            db.SetBase("a.txt", "h");
        }
        File.WriteAllText(Path.Combine(newFolder.Path, PathRules.MarkerFileName), "11111111-2222-3333-4444-555555555555\n");

        var marker = StateLocator.ReadMarker(newFolder.Path);
        Assert.Equal("11111111-2222-3333-4444-555555555555", marker);
        var found = StateLocator.FindStateDirForMarker(marker!, baseDir.Path);
        Assert.Equal(oldState, found);

        StateLocator.AdoptState(found!, newFolder.Path, baseDir.Path);
        using (var adopted = new StateDb(Path.Combine(PairnetsPaths.StateDirFor(newFolder.Path, baseDir.Path), "state.db")))
        {
            Assert.Equal(marker, adopted.MarkerId);
            Assert.Equal("h", adopted.GetFile("a.txt")!.BaseHash);
        }
        Assert.Throws<IOException>(() => StateLocator.AdoptState(found!, newFolder.Path, baseDir.Path));
        Assert.Null(StateLocator.FindStateDirForMarker(Guid.NewGuid().ToString(), baseDir.Path));
        Assert.Null(StateLocator.ReadMarker(oldFolder.Path));
    }

    [Fact]
    public void FileLoggerRotatesDailyAndKeeps14Days()
    {
        using var dir = new TempDir("logs");
        var clock = new ManualClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        File.WriteAllText(Path.Combine(dir.Path, "pairnets-20260101.log"), "old");
        File.WriteAllText(Path.Combine(dir.Path, "pairnets-20260220.log"), "recent");
        using (var provider = new RollingFileLoggerProvider(dir.Path, 14, LogLevel.Information, clock))
        {
            var log = provider.CreateLogger("Test");
            log.LogInformation("first day");
            log.LogDebug("not written");
            clock.Advance(TimeSpan.FromDays(1));
            log.LogWarning("second day");
        }
        var files = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order().ToArray();
        Assert.DoesNotContain("pairnets-20260101.log", files);
        Assert.Contains("pairnets-20260220.log", files);
        var day1 = File.ReadAllText(Path.Combine(dir.Path, $"pairnets-{clock.Now.AddDays(-1).ToLocalTime():yyyyMMdd}.log"));
        Assert.Contains("[INF] Test: first day", day1);
        Assert.DoesNotContain("not written", day1);
        Assert.Contains("[WRN] Test: second day", File.ReadAllText(Path.Combine(dir.Path, $"pairnets-{clock.Now.ToLocalTime():yyyyMMdd}.log")));
    }

    [Fact]
    public async Task FirstSyncPreviewDetectsMerge()
    {
        using var folder = new TempDir("preview");
        File.WriteAllText(Path.Combine(folder.Path, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder.Path, "Thumbs.db"), "ignored");
        var api = new FakeApi();
        var empty = await FirstSyncPreview.ComputeAsync(folder.Path, new IgnoreList(), api, default);
        Assert.Equal(1, empty.LocalFiles);
        Assert.False(empty.IsMerge);
        api.Put("b.txt", "b");
        var merge = await FirstSyncPreview.ComputeAsync(folder.Path, new IgnoreList(), api, default);
        Assert.True(merge.IsMerge);
        Assert.Contains("Nothing is deleted", FirstSyncPreview.MergeExplanation);
    }

    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string plainText) => "p:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(plainText.Reverse().ToArray())));

        public string Unprotect(string protectedText) =>
            new(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[2..])).Reverse().ToArray());
    }
}
