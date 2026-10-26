using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>
/// Where a nest is saved on this computer: the words of the add window's folder step, the Settings folder row and the
/// question before the folder changes, on your own nest and on a friend's, and that a friend's nest's new folder changes
/// nothing but this computer's own setting for it.
/// </summary>
public class FolderChoiceTextTests
{
    private static readonly NestAccess Oskars = NestAccess.Read("view", null, "Oskar", "Photos");

    [Fact]
    public void TheLineUnderTheFolderSaysItIsOnlyThisComputersOwn()
    {
        Assert.Equal("Only on this computer. Your other computers keep their own folders.", FolderChoiceText.OnlyHere(NestAccess.Full));
        Assert.Equal("Only on this computer. Oskar's folder on their computer stays where it is.", FolderChoiceText.OnlyHere(Oskars));
        Assert.Equal("Only on this computer. The owner's folder on their computer stays where it is.",
            FolderChoiceText.OnlyHere(NestAccess.Read("edit", null)));
    }

    [Fact]
    public void TheFieldNamesTheNest()
    {
        Assert.Equal("Where Photos is saved on this computer", FolderChoiceText.FieldLabel("Photos"));
        Assert.Equal("Where this nest is saved on this computer", FolderChoiceText.FieldLabel(null));
        Assert.DoesNotContain("all your computers", FolderChoiceText.StepLead, StringComparison.Ordinal); // it never was the same on all of them
    }

    [Fact]
    public void TheQuestionNamesBothFoldersAndWhatStaysTheSame()
    {
        var old = Path.Combine(Path.GetTempPath(), "Pairnets", "Photos");
        var next = Path.Combine(Path.GetTempPath(), "Elsewhere") + Path.DirectorySeparatorChar;
        Assert.Equal($"Save Photos in {Path.TrimEndingDirectorySeparator(next)} instead?", FolderChoiceText.ChangeTitle("Photos", next));

        var points = FolderChoiceText.ChangePoints(Oskars, old);
        Assert.Equal(3, points.Count);
        Assert.Contains("fills the new folder from the nest", points[0], StringComparison.Ordinal);
        Assert.Contains(old, points[1], StringComparison.Ordinal);
        Assert.Contains("no longer syncs", points[1], StringComparison.Ordinal);
        Assert.Equal("Oskar's folder on their computer doesn't change.", points[2]);

        var own = FolderChoiceText.ChangePoints(NestAccess.Full, null);
        Assert.Equal("Nothing is deleted, here or on the nest.", own[1]);
        Assert.Equal("Your other computers keep their own folders.", own[2]);
        Assert.Equal(string.Join("\n\n", own), FolderChoiceText.ChangeText(NestAccess.Full, null));
    }

    [Fact]
    public void OnlyAnotherFolderIsAChange()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Pairnets", "Photos");
        Assert.False(FolderChoiceText.IsChange(null, folder)); // none set yet: nothing to ask
        Assert.False(FolderChoiceText.IsChange(folder, folder));
        Assert.False(FolderChoiceText.IsChange(folder, " " + folder + Path.DirectorySeparatorChar));
        Assert.True(FolderChoiceText.IsChange(folder, folder + "2"));
        Assert.Equal("Saved in " + folder, FolderChoiceText.SavedIn(folder + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void AFriendsNestInANewFolderStaysTheirsAndTheOtherNestsKeepTheirFolders()
    {
        var mine = Path.Combine(Path.GetTempPath(), "Pairnets", "Home");
        var theirs = Path.Combine(Path.GetTempPath(), "Pairnets", "Photos");
        var moved = Path.Combine(Path.GetTempPath(), "Elsewhere", "Photos");
        var all = new ClientSettings { NestId = "first", NestLabel = "Home", ServerUrl = "https://sync.pairnets.app/n/nst_a/", Folder = mine };
        var friend = all.BlankNest();
        friend.NestLabel = "Photos";
        friend.ServerUrl = "https://sync.pairnets.app/n/nst_b/";
        friend.Folder = theirs;
        friend.Access = NestAccess.View;
        friend.SharedFolders = ["Trip"];
        friend.SharedBy = "Oskar";
        var id = all.AddNest(friend);

        var saved = all.ForNest(id)!.Clone();
        saved.Folder = moved;
        var next = SettingsSwitches.WithNestSaved(all, saved);

        var after = next.ForNest(id)!;
        Assert.Equal(moved, after.Folder);
        Assert.Equal(Oskars.Level, after.ShareAccess().Level);
        Assert.Equal("Oskar", after.ShareAccess().SharedBy);
        Assert.Equal(["Trip"], after.ShareAccess().Folders!);
        Assert.Equal(mine, next.ForNest("first")!.Folder);
    }
}
