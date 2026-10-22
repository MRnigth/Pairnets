using System.Text.Json;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// Friend invites in the apps (1.0.61.2): reading what this computer may do on a nest, which paths a share covers, the
/// held-back rule, the settings file, and the words and rows the windows show.
/// </summary>
public class NestAccessTests
{
    private const string NestId = "nst_0123456789abcdefghjkmnpqrs";

    // ------------------------------------------------------------------ reading the access

    [Fact]
    public void MissingAccessIsTheOwnersAndAnUnknownOneIsViewOnly()
    {
        Assert.Equal(NestAccess.Full, NestAccess.Read(null, null));
        Assert.Equal(NestAccess.Full, NestAccess.Read("owner", ["Photos"])); // the owner never has folders
        Assert.Equal(NestAccess.View, NestAccess.Read("admin", null).Level); // never more than the app understands
        Assert.True(NestAccess.Read("edit", null).CanWrite);
        Assert.False(NestAccess.Read("view", null).CanWrite);
        Assert.True(NestAccess.Read(" view ", null).IsViewOnly);
    }

    [Fact]
    public void FolderNamesThatAreNotPlainTopLevelNamesAreDropped()
    {
        var access = NestAccess.Read("edit", ["Photos", "a/b", "..", "", "Photos", "School", "back\\slash", null]);
        Assert.Equal(["Photos", "School"], access.Folders!);
        // Folders given, none usable: nothing is shared (never the whole nest).
        Assert.Empty(NestAccess.Read("view", ["../x"]).Folders!);
        Assert.False(NestAccess.Read("view", ["../x"]).Allows("Photos/a.jpg"));
    }

    [Theory]
    [InlineData("Photos", true)]
    [InlineData("Photos/2026/trip.jpg", true)]
    [InlineData("School/notes.docx", true)]
    [InlineData("Photos2/a.jpg", false)]
    [InlineData("photos/a.jpg", false)] // exact case, as the nest checks it
    [InlineData("Music/a.mp3", false)]
    [InlineData("top.txt", false)]
    [InlineData("", false)]
    public void APathIsInTheShareWhenItIsOrLiesUnderAFolder(string path, bool inside)
    {
        Assert.Equal(inside, NestAccess.Read("edit", ["Photos", "School"]).Allows(path));
        Assert.True(NestAccess.Read("edit", null).Allows(path)); // the whole nest
    }

    [Fact]
    public void EqualAccessesAreEqualWhateverListHoldsTheFolders()
    {
        Assert.Equal(new NestAccess("view", ["Photos"], "Oskar Nilsson"), new NestAccess("view", new List<string> { "Photos" }, "Oskar Nilsson"));
        Assert.NotEqual(new NestAccess("view", ["Photos"]), new NestAccess("view", null));
        Assert.NotEqual(new NestAccess("view"), new NestAccess("edit"));
    }

    // ------------------------------------------------------------------ the held-back rule

    [Theory]
    // local, server, base → held back on a view-only nest
    [InlineData("L2", "S1", "S1", true)]   // edited here
    [InlineData("L2", "S2", "S1", true)]   // edited here, and on the nest
    [InlineData("L1", null, null, true)]   // made here
    [InlineData(null, "S1", "S1", true)]   // deleted here
    [InlineData(null, "S2", "S1", true)]   // deleted here, changed on the nest
    [InlineData("S1", "S2", "S1", false)]  // only the nest changed: it comes down
    [InlineData("S1", null, "S1", false)]  // deleted on the nest: it goes here too
    [InlineData("S2", "S2", "S1", false)]  // the same on both sides
    [InlineData(null, "S1", null, false)]  // new on the nest
    public void AChangeMadeHereIsHeldBack(string? local, string? server, string? @base, bool held) =>
        Assert.Equal(held, NestAccess.IsHeldBack(local, server, @base));

    [Fact]
    public void WhatEachAccessDoesWithAPath()
    {
        var view = NestAccess.Read("view", null);
        Assert.Equal(ShareStep.HeldBack, view.StepFor("a.txt", SyncAction.Upload, "L2", "S1", "S1", putBack: false));
        Assert.Equal(ShareStep.PutBack, view.StepFor("a.txt", SyncAction.Upload, "L2", "S1", "S1", putBack: true));
        Assert.Equal(ShareStep.HeldBack, view.StepFor("a.txt", SyncAction.DeleteRemote, null, "S1", "S1", putBack: false));
        Assert.Equal(ShareStep.HeldBack, view.StepFor("a.txt", SyncAction.Conflict, "L2", "S2", "S1", putBack: false));
        Assert.Equal(ShareStep.AsPlanned, view.StepFor("a.txt", SyncAction.Download, "S1", "S2", "S1", putBack: false));
        Assert.Equal(ShareStep.AsPlanned, view.StepFor("a.txt", SyncAction.DeleteLocal, "S1", null, "S1", putBack: true));

        var photos = NestAccess.Read("edit", ["Photos"]);
        Assert.Equal(ShareStep.AsPlanned, photos.StepFor("Photos/a.jpg", SyncAction.Upload, "L1", null, null, putBack: false));
        Assert.Equal(ShareStep.LocalOnly, photos.StepFor("Notes/a.txt", SyncAction.Upload, "L1", null, null, putBack: false));
        Assert.Equal(ShareStep.LocalOnly, photos.StepFor("Notes/a.txt", SyncAction.Download, null, "S1", null, putBack: false));
        Assert.Equal(ShareStep.Forget, photos.StepFor("Notes/a.txt", SyncAction.DeleteLocal, "S1", null, "S1", putBack: false));

        Assert.Equal(ShareStep.AsPlanned, NestAccess.Full.StepFor("x", SyncAction.DeleteRemote, null, "S1", "S1", putBack: false));
    }

    // ------------------------------------------------------------------ the settings file

    [Fact]
    public void ASharedNestKeepsItsAccessInTheSettingsFileAndAnOldFileIsTheOwners()
    {
        using var dir = new TempDir("access");
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "serverUrl": "https://nest.example.com/", "protectedToken": "x", "folder": "C:\\Sync", "deviceName": "PC" }""");
        Assert.Equal(NestAccess.Full, SettingsStore.Load(path).ShareAccess());

        var s = new ClientSettings { ServerUrl = "https://nest.example.com/", ProtectedToken = "x", Folder = Path.Combine(dir.Path, "Own"), DeviceName = "PC" };
        var shared = s.BlankNest();
        shared.ServerUrl = $"https://sync.pairnets.app/n/{NestId}/";
        shared.Folder = Path.Combine(dir.Path, "Home nest");
        shared.DeviceName = "PC";
        shared.NestLabel = "Home nest";
        shared.Access = "view";
        shared.SharedFolders = ["Photos", "School"];
        shared.SharedBy = "Oskar Nilsson";
        var id = s.AddNest(shared);
        SettingsStore.Save(path, s);

        var back = SettingsStore.Load(path);
        Assert.Equal(NestAccess.Full, back.ShareAccess()); // the first nest is still its own
        var nest = back.ForNest(id)!;
        Assert.Equal(new NestAccess("view", ["Photos", "School"], "Oskar Nilsson", "Home nest"), nest.ShareAccess());

        // A change the nest reports goes back into its own profile, and leaving the first nest moves it up whole.
        nest.SharedFolders = ["Photos"];
        Assert.True(back.PutNest(nest));
        back.RemoveNest(back.NestId!);
        Assert.Equal(["Photos"], back.ShareAccess().Folders!);
        Assert.Equal("Oskar Nilsson", back.SharedBy);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("view", json.RootElement.GetProperty("otherNests")[0].GetProperty("access").GetString());
    }

    [Fact]
    public void ASignInOnTheNestItselfIsAlwaysTheOwners()
    {
        var before = new ClientSettings { Access = "view", SharedFolders = ["Photos"], SharedBy = "Oskar Nilsson" };
        var after = Nest.SettingsAfterSignIn(before, new Uri("https://nest.example.com/"), new DeviceKeyGrant("dev_1", "PC", "key"), "C:\\Sync", false, "protected");
        Assert.Equal(NestAccess.Full, after.ShareAccess());
        Assert.Null(after.SharedBy);
    }

    [Fact]
    public void AnAccountSignInToASharedNestSavesWhatWasShared()
    {
        var result = new AccountSignInResult(new Uri($"https://sync.pairnets.app/n/{NestId}/"), NestId, "Home nest", new DeviceKeyGrant("dev_1", "PC", "key"),
            "jonas@example.com", NestAccess.Read("edit", ["Music"], "Oskar Nilsson", "Home nest"));
        var settings = AccountSignIn.SettingsAfterSignIn(null, result, "C:\\Home nest", false, "protected");
        Assert.Equal("edit", settings.Access);
        Assert.Equal(["Music"], settings.SharedFolders!);
        Assert.Equal("Oskar Nilsson", settings.SharedBy);

        var own = AccountSignIn.SettingsAfterSignIn(settings, result with { Access = null }, "C:\\Own", false, "protected");
        Assert.Null(own.Access);
        Assert.Null(own.SharedFolders);
    }

    // ------------------------------------------------------------------ what the windows show

    private static StatusSnapshot Owners(PeopleAnswer? people) => StatusSnapshot.Initial with
    {
        IsRelay = true,
        ServerUrl = $"https://sync.pairnets.app/n/{NestId}/",
        People = people,
    };

    [Fact]
    public void ThePeopleSectionListsFriendsAndThoseWaiting()
    {
        var people = PeopleSection.From(Owners(new PeopleAnswer(
        [
            new FriendPerson("acc_anna", "Anna Berg · a•••@gmail.com", "edit", ["Photos", "School"], [new FriendDevice("d1", "ANNA-LAPTOP", null, null), new FriendDevice("d2", "ANNA-PC", null, null)]),
            new FriendPerson("acc_jonas", "Jonas", "view", null, [new FriendDevice("d3", "JONAS-PC", null, null)]),
        ],
        [new FriendRequestEntry("inv_1", "Oskar Nilsson", "oskar.n@example.com", "view", ["Music"])])));

        Assert.True(people.Visible);
        Assert.Equal("2 friends · 1 waiting for your OK", people.Summary);
        Assert.Equal(3, people.Rows.Count);
        var anna = people.Rows[0];
        Assert.Equal(("A", "Anna Berg", "a•••@gmail.com · 2 computers"), (anna.Letter, anna.Title, anna.Line));
        Assert.Equal(["Photos", "School"], anna.Chips);
        Assert.Equal("Can change", anna.Pill);
        Assert.Equal("Remove Anna", anna.RemoveLabel);
        var jonas = people.Rows[1];
        Assert.Equal(["Whole nest"], jonas.Chips);
        Assert.Equal("View only", jonas.Pill);
        Assert.Equal("1 computer", jonas.Line);
        var oskar = people.Rows[2];
        Assert.True(oskar.Waiting);
        Assert.Equal("Oskar Nilsson wants to join", oskar.Title);
        Assert.Equal("Let Oskar in", oskar.LetInLabel);
        Assert.Equal("oskar.n@example.com used your invite link · Music · view only · waiting for your OK", oskar.Line);
        Assert.Equal($"https://sync.pairnets.app/account#people-{NestId}", people.PeopleUrl);
    }

    [Fact]
    public void ThePeopleRowsReadAgainAreEqual_SoThePagesDoNotRedrawAndTakeTheFocus()
    {
        // The nest's answer read anew (as every refresh does): new lists with the same people in them.
        PeopleSection Read(string[] folders) => PeopleSection.From(Owners(new PeopleAnswer(
            [new FriendPerson("acc_anna", "Anna Berg · a•••@gmail.com", "edit", [.. folders], [new FriendDevice("d1", "ANNA-LAPTOP", null, null)])],
            [new FriendRequestEntry("inv_1", "Oskar Nilsson", "oskar.n@example.com", "view", ["Music"])])));

        Assert.True(Read(["Photos", "School"]).Rows.SequenceEqual(Read(["Photos", "School"]).Rows));
        Assert.Equal(Read(["Photos"]).Rows[0].GetHashCode(), Read(["Photos"]).Rows[0].GetHashCode());
        // A real change still redraws.
        Assert.False(Read(["Photos", "School"]).Rows.SequenceEqual(Read(["Photos", "Music"]).Rows));
    }

    [Fact]
    public void ThePeopleSectionIsOnlyForTheOwnerOfANestReachedThroughPairnets()
    {
        Assert.Equal("Only you so far", PeopleSection.From(Owners(null)).Summary);
        Assert.False(PeopleSection.From(Owners(null) with { IsRelay = false, ServerUrl = "https://nest.example.com/" }).Visible);
        Assert.False(PeopleSection.From(Owners(null) with { Access = NestAccess.Read("edit", null) }).Visible);
    }

    [Fact]
    public void FriendsComputersAreLeftOutOfTheComputersGrid()
    {
        var now = DateTimeOffset.UtcNow;
        var s = Owners(null) with
        {
            Devices =
            [
                new DeviceInfo("DESKTOP", now, now, true, Access: "owner"),
                new DeviceInfo("ANNA-LAPTOP", now, now, true, Id: "dev_anna", Access: "edit", Person: "Anna Berg · a•••@gmail.com"),
                new DeviceInfo("OLD-PC", now, now, true), // a nest from before friends says nothing: the owner's
            ],
        };
        Assert.Equal(["DESKTOP", "OLD-PC"], DeviceCard.From(s, "DESKTOP", now).Select(c => c.Name));
        // On a friend's computer, the friend's own card stays.
        Assert.Contains(DeviceCard.From(s, "ANNA-LAPTOP", now), c => c.Name == "ANNA-LAPTOP" && c.IsThis);
    }

    [Fact]
    public void TheInviteWindowHandsOverToTheAccountPage()
    {
        Assert.Equal($"https://sync.pairnets.app/account?invite={NestId}&access=view", Relay.InviteUrl(NestId, null, canChange: false));
        // One folders= per folder: a comma inside a name stays part of it.
        Assert.Equal($"https://sync.pairnets.app/account?invite={NestId}&folders=Photos%2C%202026&folders=School%20work&access=edit",
            Relay.InviteUrl(NestId, ["Photos, 2026", "School work"], canChange: true));
        Assert.Null(InviteChoices.Problem(wholeNest: true, []));
        Assert.NotNull(InviteChoices.Problem(wholeNest: false, []));
        Assert.Null(InviteChoices.Problem(wholeNest: false, ["Photos"]));
    }

    [Fact]
    public void TheInviteWindowsFolderListSaysSizesAndHowManyAreChosen()
    {
        Assert.Equal("Empty", InviteChoices.SizeText(0));
        Assert.Equal("362 MB", InviteChoices.SizeText(380_000_000));
        Assert.Equal("None chosen yet", InviteChoices.ChosenSummary(0, 4, 0));
        Assert.Equal("2 of 4 chosen · 2.59 GB", InviteChoices.ChosenSummary(2, 4, 2_780_000_000));
        Assert.Equal("1 of 4 chosen", InviteChoices.ChosenSummary(1, 4, 0)); // an empty folder: no "0 B"
        Assert.False(InviteChoices.AllTicked(0, 4));
        Assert.Null(InviteChoices.AllTicked(2, 4)); // some: the dash
        Assert.True(InviteChoices.AllTicked(4, 4));
    }

    [Fact]
    public void TheInviteWindowOffersThisComputersTopLevelFoldersWithTheirSizes()
    {
        using var dir = new TempDir("invite");
        Directory.CreateDirectory(Path.Combine(dir.Path, "School"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "Photos", "2026"));
        File.WriteAllBytes(Path.Combine(dir.Path, "Photos", "2026", "trip.jpg"), new byte[1500]);
        File.WriteAllBytes(Path.Combine(dir.Path, "Photos", "a.jpg"), new byte[500]);
        Directory.CreateDirectory(Path.Combine(dir.Path, ".pairnets-tmp"));
        File.WriteAllText(Path.Combine(dir.Path, "top.txt"), "not a folder");

        Assert.Equal([("Photos", 2000L), ("School", 0L)], InviteChoices.LocalFolders(dir.Path));
        Assert.Empty(InviteChoices.LocalFolders(Path.Combine(dir.Path, "missing")));
    }

    [Fact]
    public void AFriendsNestSaysWhoseItIsAndWhatIsHeldBack()
    {
        var access = NestAccess.Read("view", ["Photos", "School"], "Oskar Nilsson", "Home nest");
        Assert.Equal("Shared by Oskar Nilsson · view only", ShareText.SharedLine(access));
        Assert.Equal("Photos, School", ShareText.Folders(access.Folders));
        Assert.Equal("Whole nest", ShareText.Folders(null));
        Assert.Equal("Put back Oskar Nilsson's version", ShareText.PutBack(access));
        Assert.Equal("3 changes stay on this computer", ShareText.HeldBackTitle(3));
        Assert.Equal("1 change stays on this computer", ShareText.HeldBackTitle(1));
        Assert.Equal("You can only view Home nest, so your edits to trip.jpg, notes.docx and 1 more aren't sent to Oskar Nilsson. "
            + "Put back Oskar Nilsson's version, or keep yours (it stays only here and won't sync).",
            ShareText.HeldBackDetail(access, "Home nest", ["Photos/trip.jpg", "School/notes.docx", "School/x.txt"]));
        Assert.StartsWith("Oskar Nilsson stopped sharing Home nest.", ShareText.StoppedSharing(access));
    }

    [Fact]
    public void TheNestListAndTheOverviewSayWhoSharedTheNest()
    {
        var all = new ClientSettings
        {
            ServerUrl = $"https://sync.pairnets.app/n/{NestId}/", ProtectedToken = "x", Folder = "C:\\Home nest", DeviceName = "PC",
            NestLabel = "Home nest", Access = "view", SharedBy = "Oskar Nilsson",
        };
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = DateTimeOffset.UtcNow, NestLabel = "Home nest", Access = all.ShareAccess() };
        var row = Assert.Single(NestListModel.Build(all, [(all, idle)], null).Rows);
        Assert.Equal("Shared by Oskar Nilsson · view only", row.Sub);
        Assert.Equal("Up to date · Shared by Oskar Nilsson · view only", row.CardLine);

        var overview = OverviewModel.Build(idle, "PC", "C:\\Home nest", 0, [], DateTimeOffset.UtcNow);
        Assert.Equal(ShareText.ViewOnly, overview.Badge);
        Assert.Equal(["Shared by", "You get", "On this computer in"], overview.Facts.Select(f => f.Label));
        Assert.Equal(["Oskar Nilsson", "Whole nest", "C:\\Home nest"], overview.Facts.Select(f => f.Value));

        var removed = idle with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.SignedOut };
        Assert.Equal("Oskar Nilsson stopped sharing Home nest", OverviewModel.Build(removed, "PC", "C:\\Home nest", 0, [], DateTimeOffset.UtcNow).Title);
    }
}
