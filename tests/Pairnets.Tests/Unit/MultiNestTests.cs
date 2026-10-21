using System.Text.Json;
using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Several nests at once (docs/DECISIONS.md): the settings file, the folder rules, key slots and the nest list.</summary>
public class MultiNestTests
{
    private static ClientSettings OneNest() => new()
    {
        ServerUrl = "https://sync.pairnets.app/n/nst_aaaaaaaaaaaaaaaaaaaaaaaaaa/",
        ProtectedToken = "keychain:v1",
        DeviceId = "dev_1",
        Folder = Path.Combine(Path.GetTempPath(), "pn-home"),
        DeviceName = "StudyPC",
        AccountEmail = "you@example.com",
        FirstRunCompleted = true,
        UploadLimitMBps = 5,
    };

    private static ClientSettings NestAt(ClientSettings all, string url, string folder, string name, string? label = null)
    {
        var blank = all.BlankNest();
        blank.ServerUrl = url;
        blank.ProtectedToken = "keychain:v1:" + blank.NestId;
        blank.Folder = folder;
        blank.DeviceName = name;
        blank.NestLabel = label;
        return blank;
    }

    [Fact]
    public void AnOldSettingsFileIsOneNestAndReadsAsBefore()
    {
        using var dir = new TempDir("multinest");
        var path = Path.Combine(dir.Path, "settings.json");
        // A settings file from 1.0.60: no nest fields at all.
        File.WriteAllText(path, """{ "serverUrl": "https://nest.example.com/", "protectedToken": "x", "folder": "C:\\Sync", "deviceName": "PC", "firstRunCompleted": true }""");
        var s = SettingsStore.Load(path);
        Assert.Equal(1, s.NestCount);
        var nest = Assert.Single(s.Nests());
        Assert.Equal("https://nest.example.com/", nest.ServerUrl);
        Assert.Equal("C:\\Sync", nest.Folder);
        Assert.False(string.IsNullOrEmpty(nest.NestId));
        Assert.Equal(nest.NestId, s.NestId); // the id was given to the first nest itself
    }

    [Fact]
    public void ASecondNestIsSavedNextToTheFirstWhichAnOlderAppStillReads()
    {
        using var dir = new TempDir("multinest");
        var path = Path.Combine(dir.Path, "settings.json");
        var s = OneNest();
        var work = NestAt(s, "https://work.example.com/", Path.Combine(dir.Path, "Work"), "STUDY-LAPTOP", "Work nest");
        var id = s.AddNest(work);
        SettingsStore.Save(path, s);

        var back = SettingsStore.Load(path);
        Assert.Equal(2, back.NestCount);
        var nests = back.Nests();
        Assert.Equal("StudyPC", nests[0].DeviceName);
        Assert.Equal("STUDY-LAPTOP", nests[1].DeviceName);
        Assert.Equal(id, nests[1].NestId);
        Assert.Equal("Work nest", nests[1].NestLabel);
        // App-wide values belong to every nest.
        Assert.Equal(5, nests[1].UploadLimitMBps);
        Assert.Empty(nests[1].OtherNests);

        // The first nest stays in the top-level fields, exactly where Pairnets 1.0.60 reads them.
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("https://sync.pairnets.app/n/nst_aaaaaaaaaaaaaaaaaaaaaaaaaa/", json.RootElement.GetProperty("serverUrl").GetString());
        Assert.Equal("StudyPC", json.RootElement.GetProperty("deviceName").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("otherNests").GetArrayLength());
    }

    [Fact]
    public void PutNestChangesOnlyThatNest()
    {
        var s = OneNest();
        var id = s.AddNest(NestAt(s, "https://work.example.com/", "D:\\Work", "W"));
        var work = s.ForNest(id)!;
        work.DeviceName = "Renamed";
        work.UploadLimitMBps = 99; // app-wide: not taken from a nest
        Assert.True(s.PutNest(work));
        Assert.Equal("Renamed", s.ForNest(id)!.DeviceName);
        Assert.Equal("StudyPC", s.DeviceName);
        Assert.Equal(5, s.UploadLimitMBps);
    }

    [Fact]
    public void LeavingTheFirstNestMovesTheNextOneUp()
    {
        var s = OneNest();
        var first = s.Nests()[0].NestId!;
        var id = s.AddNest(NestAt(s, "https://work.example.com/", "D:\\Work", "W", "Work nest"));
        s.RemoveNest(first);
        Assert.Equal(1, s.NestCount);
        Assert.Equal(id, s.NestId);
        Assert.Equal("https://work.example.com/", s.ServerUrl);
        Assert.Equal("Work nest", s.NestLabel);
        Assert.Empty(s.OtherNests);

        s.RemoveNest(id);
        Assert.Equal(0, s.NestCount);
        Assert.False(s.HasFirstNest);
        Assert.Null(s.Folder);
    }

    [Fact]
    public void KeySlotsKeepTheFirstNestWhereItWasAndGiveOthersTheirOwn()
    {
        var s = OneNest();
        s.EnsureNestIds();
        Assert.Null(s.KeySlotFor(s));
        var blank = s.BlankNest();
        Assert.Equal(blank.NestId, s.KeySlotFor(blank));
        Assert.True(SecretSlots.IsSlot(blank.NestId));
        Assert.NotEqual(s.NestId, blank.NestId);
        Assert.Null(new ClientSettings().KeySlotFor(new ClientSettings().BlankNest())); // no nest yet: the usual place

        Assert.Equal("keychain:v1", SecretSlots.Marker("keychain:v1", null));
        Assert.Equal("keychain:v1:n1234abcd", SecretSlots.Marker("keychain:v1", "n1234abcd"));
        Assert.True(SecretSlots.TryRead("keychain:v1", "keychain:v1:n1234abcd", out var slot));
        Assert.Equal("n1234abcd", slot);
        Assert.True(SecretSlots.TryRead("keychain:v1", "keychain:v1", out slot));
        Assert.Null(slot);
        Assert.False(SecretSlots.TryRead("keychain:v1", "keychain:v1:../x", out _));
        Assert.False(SecretSlots.TryRead("keychain:v1", "libsecret:v1", out _));
        Assert.Throws<ArgumentException>(() => SecretSlots.Marker("file:v1", "Bad Slot"));
    }

    [Theory]
    [InlineData("Home", "already uses this folder")]
    [InlineData("Home\\Work", "inside Home nest's folder")]
    [InlineData("", "inside this one")]
    public void AFolderCannotBeAnotherNestsOrInsideOrAroundOne(string relative, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pn-rules");
        var home = Path.Combine(root, "Home");
        var problem = NestFolders.Problem(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)), [("Home nest", home)]);
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void AFolderNextToAnotherNestsIsFine()
    {
        var root = Path.Combine(Path.GetTempPath(), "pn-rules");
        Assert.Null(NestFolders.Problem(Path.Combine(root, "Work"), [("Home nest", Path.Combine(root, "Home"))]));
        Assert.Null(NestFolders.Problem(Path.Combine(root, "Home2"), [("Home nest", Path.Combine(root, "Home"))])); // a prefix is not "inside"
    }

    [Fact]
    public void TheSuggestedFolderIsPairnetsAndTheNestsNameAndStaysClear()
    {
        var home = Path.Combine(Path.GetTempPath(), "pn-home-dir");
        Assert.Equal(Path.Combine(home, "Pairnets", "Work nest"), NestFolders.Suggested("Work nest", [], home));
        Assert.Equal(Path.Combine(home, "Pairnets", "Work nest (2)"), NestFolders.Suggested("Work nest", [Path.Combine(home, "Pairnets", "Work nest")], home));
        Assert.Equal(Path.Combine(home, "Pairnets", "a-b"), NestFolders.Suggested("a/b", [], home));
    }

    [Fact]
    public void TheTrayShowsTheNestInTheWorstState()
    {
        Assert.Equal(RunnerStatus.Idle, NestListModel.WorstOf([RunnerStatus.Idle, RunnerStatus.Idle]));
        Assert.Equal(RunnerStatus.Syncing, NestListModel.WorstOf([RunnerStatus.Idle, RunnerStatus.Syncing]));
        Assert.Equal(RunnerStatus.Paused, NestListModel.WorstOf([RunnerStatus.Syncing, RunnerStatus.Paused]));
        Assert.Equal(RunnerStatus.Offline, NestListModel.WorstOf([RunnerStatus.Paused, RunnerStatus.Offline]));
        Assert.Equal(RunnerStatus.Blocked, NestListModel.WorstOf([RunnerStatus.Offline, RunnerStatus.Blocked]));
        Assert.Equal(RunnerStatus.Error, NestListModel.WorstOf([RunnerStatus.Blocked, RunnerStatus.Error, RunnerStatus.Idle]));
    }

    [Fact]
    public void TheNestListNamesEachNestWithItsFolderAndState()
    {
        var home = Path.Combine(Path.GetTempPath(), "pn-user");
        var s = OneNest();
        s.Folder = Path.Combine(home, "Pairnets", "Home nest");
        s.NestLabel = "Home nest";
        var id = s.AddNest(NestAt(s, "https://work.example.com/", Path.Combine(Path.GetPathRoot(home)!, "Work"), "W", "Work nest"));
        var nests = s.Nests();
        var syncing = StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, Text = "Syncing" };
        syncing = syncing with { PassBytesDone = 40, PassBytesTotal = 100 };
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date" };
        var m = NestListModel.Build(s, [(nests[0], idle), (nests[1], syncing)], id, home);

        Assert.True(m.Visible);
        Assert.False(m.AllSelected);
        Assert.Equal(RunnerStatus.Syncing, m.Worst);
        Assert.Equal("2 nests · 1 syncing", m.AllSub);
        Assert.Equal("Home nest", m.Rows[0].Title);
        Assert.Equal(Path.Combine("Pairnets", "Home nest") + " · up to date", m.Rows[0].Sub);
        Assert.Equal("Work nest", m.Rows[1].Title);
        Assert.Equal("syncing 40%", m.Rows[1].Text);
        Assert.True(m.Rows[1].Selected);
        Assert.Equal("Pairnets\nHome nest: up to date\nWork nest: syncing 40%", m.Tooltip);

        var one = NestListModel.Build(OneNest(), [(OneNest(), idle)], null);
        Assert.False(one.Visible); // with one nest the left side looks as it always did
    }

    [Fact]
    public void NestsWithoutANameAreToldApart()
    {
        var s = OneNest();
        s.AddNest(NestAt(s, "https://sync.pairnets.app/n/nst_bbbbbbbbbbbbbbbbbbbbbbbbbb/", "D:\\B", "W"));
        s.AddNest(NestAt(s, "https://nest.example.org/", "D:\\C", "W"));
        var nests = s.Nests();
        Assert.Equal("Your nest", NestNames.Title(nests[0], s));
        Assert.Equal("Nest 2", NestNames.Title(nests[1], s));
        Assert.Equal("nest.example.org", NestNames.Title(nests[2], s));
        Assert.Equal("Nest 2: ", NestNames.Prefix(nests[1], s));
        Assert.Equal("", NestNames.Prefix(OneNest(), OneNest()));
    }

    [Fact]
    public void AddingANestKnowsWhichNestsAreAlreadyHere()
    {
        var s = OneNest();
        var blank = s.BlankNest();
        var adding = new NestAdding(s, blank);
        Assert.Equal(["nst_aaaaaaaaaaaaaaaaaaaaaaaaaa"], adding.Joined);
        Assert.NotNull(adding.AlreadySynced(new Uri("https://sync.pairnets.app/n/nst_aaaaaaaaaaaaaaaaaaaaaaaaaa")));
        Assert.Null(adding.AlreadySynced(new Uri("https://sync.pairnets.app/n/nst_cccccccccccccccccccccccccc/")));
        Assert.Contains("already uses this folder", adding.FolderProblem(s.Folder!));
    }

    [Fact]
    public void ResetForgetsEveryNestsKey()
    {
        using var dir = new TempDir("multinest-reset");
        var s = OneNest();
        s.AddNest(NestAt(s, "https://work.example.com/", Path.Combine(dir.Path, "Work"), "W"));
        var protector = new RecordingProtector();
        LocalReset.Run(s, protector, Path.Combine(dir.Path, "settings.json"), dir.Path);
        Assert.Equal(2, protector.Forgotten.Count);
        Assert.Contains("keychain:v1", protector.Forgotten);
    }

    [Fact]
    public void TwoNestsConnectionsShareOneSpeedLimit()
    {
        var limits = new SpeedLimits();
        using var a = new PairnetsApiClient(new Uri("https://a.example.com/"), "t", "pc");
        using var b = new PairnetsApiClient(new Uri("https://b.example.com/"), "t", "pc");
        a.ShareLimits(limits.Upload, limits.Download);
        b.ShareLimits(limits.Upload, limits.Download);
        limits.Upload.SetMegabytesPerSecond(2);
        Assert.Same(a.UploadLimit, b.UploadLimit);
        Assert.Equal(2L * 1024 * 1024, b.UploadLimit.BytesPerSecond);
    }

    [Fact]
    public void TheBrowserPageIsToldWhichNestsThisComputerHas()
    {
        // The have= list goes on the sign-in link; only real nest ids, at most 20.
        var link = typeof(AccountSignIn).GetMethod("BrowserLink", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(link);
        var flow = new AccountSignIn("PC", "google", joined: ["nst_aaaaaaaaaaaaaaaaaaaaaaaaaa", "not-a-nest"]);
        var start = Activator.CreateInstance(link!.GetParameters()[0].ParameterType, null, "ABCD-EFGH", null, null, null, null)!;
        var url = (string)link.Invoke(flow, [start])!;
        Assert.EndsWith("&method=google&have=nst_aaaaaaaaaaaaaaaaaaaaaaaaaa", url);
    }

    private sealed class RecordingProtector : ISecretProtector
    {
        public List<string> Forgotten { get; } = [];

        public string Protect(string plainText) => plainText;

        public string Unprotect(string protectedText) => protectedText;

        public void Forget(string protectedText) => Forgotten.Add(protectedText);
    }
}
