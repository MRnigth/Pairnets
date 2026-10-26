using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>A nest's folder can't be one the system keeps for itself (C:\Program Files...) or one Pairnets may not save in.</summary>
public class SyncFolderCheckTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "pn-system");
    private static readonly string[] SystemDirs = [Path.Combine(Root, "Program Files"), Path.Combine(Root, "Windows")];

    [Fact]
    public void ASystemFolderAndEverythingInsideItIsRefused()
    {
        Assert.NotNull(SyncFolderCheck.SystemFolderOf(Path.Combine(Root, "Program Files"), SystemDirs));
        Assert.NotNull(SyncFolderCheck.SystemFolderOf(Path.Combine(Root, "Program Files") + Path.DirectorySeparatorChar, SystemDirs));
        Assert.NotNull(SyncFolderCheck.SystemFolderOf(Path.Combine(Root, "Program Files", "Pairnets", "Home"), SystemDirs));
        Assert.NotNull(SyncFolderCheck.SystemFolderOf(Path.Combine(Root, "Windows", "Sync"), SystemDirs));
    }

    [Fact]
    public void AFolderBesideASystemFolderIsFine()
    {
        Assert.Null(SyncFolderCheck.SystemFolderOf(Path.Combine(Root, "Program FilesX"), SystemDirs));
        Assert.Null(SyncFolderCheck.SystemFolderOf(Path.Combine(Root, "Users", "you", "Pairnets"), SystemDirs));
        Assert.Null(SyncFolderCheck.SystemFolderOf(Root, SystemDirs));
    }

    [Fact]
    public void ThisComputersProgramFilesFolderIsASystemFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.NotNull(SyncFolderCheck.SystemFolderOf("/usr/local/share/sync"));
            return;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.NotNull(SyncFolderCheck.SystemFolderOf(Path.Combine(programFiles, "Pairnets")));
        Assert.NotNull(SyncFolderCheck.SystemFolderOf(Path.Combine(programFiles.ToLowerInvariant(), "My nest")));
        Assert.NotNull(SyncFolderCheck.SystemFolderOf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "Sync")));
        Assert.Null(SyncFolderCheck.SystemFolderOf(SyncFolderCheck.DefaultFolder));
    }

    [Fact]
    public void TheWordsNameTheFolderAndOfferAnother()
    {
        var problem = SyncFolderCheck.Problem(Path.Combine(Root, "Program Files", "Sync"), Path.Combine(Root, "Users", "you", "Pairnets"), SystemDirs);
        Assert.NotNull(problem);
        Assert.Contains(Path.Combine(Root, "Program Files"), problem);
        Assert.Contains(Path.Combine(Root, "Users", "you", "Pairnets"), problem);
        Assert.DoesNotContain("Exception", problem);
    }

    [Fact]
    public void AFolderOfYourOwnIsFineWhetherOrNotItIsThereYet()
    {
        using var dir = new TempDir("foldercheck");
        Assert.True(SyncFolderCheck.CanWrite(dir.Path));
        Assert.True(SyncFolderCheck.CanWrite(Path.Combine(dir.Path, "new", "nest")));
        Assert.Null(SyncFolderCheck.Problem(dir.Path, systemFolders: SystemDirs));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Path)); // the test file and test folder are gone again
    }

    [Fact]
    public void AFolderPairnetsMayNotSaveInIsRefused()
    {
        using var dir = new TempDir("foldercheck");
        using var denied = WriteDenied.On(dir.Path);
        if (denied is null)
            return; // this account may save anywhere (root on Linux): nothing to check
        Assert.False(SyncFolderCheck.CanWrite(dir.Path));
        var problem = SyncFolderCheck.Problem(dir.Path, systemFolders: SystemDirs);
        Assert.NotNull(problem);
        Assert.Contains(SyncFolderCheck.DefaultFolder, problem);
    }

    [Fact]
    public void TheNestFolderRulesIncludeIt()
    {
        var settings = new ClientSettings
        {
            ServerUrl = "https://sync.pairnets.app/n/nst_aaaaaaaaaaaaaaaaaaaaaaaaaa/",
            ProtectedToken = "keychain:v1",
            DeviceName = "StudyPC",
            Folder = Path.Combine(Path.GetTempPath(), "pn-home"),
        };
        var protectedFolder = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pairnets nest")
            : "/usr/share/pairnets-nest";
        Assert.NotNull(settings.FolderProblem(protectedFolder, nestId: null));
        Assert.Null(settings.FolderProblem(settings.Folder, settings.NestId));
    }

    [Fact]
    public void TheCardOffersAnotherFolder()
    {
        var s = StatusSnapshot.Initial with { Status = RunnerStatus.Blocked, BlockReason = BlockReason.FolderNotWritable, Text = "Pairnets can't save files in C:\\Program Files\\Sync." };
        Assert.Equal("Choose another folder…", s.FixLabel);
        Assert.Equal("Can't save in your folder", s.Headline);
    }

    [Fact]
    public void TheTestFileIsNeverSynced()
    {
        Assert.Equal(Pairnets.Core.Paths.PathProblem.ReservedName, Pairnets.Core.Paths.PathRules.Check(Pairnets.Core.Paths.PathRules.ProbeFileName));
        Assert.True(new Pairnets.Core.Paths.IgnoreList([]).IsIgnored(Pairnets.Core.Paths.PathRules.ProbeFileName));
    }
}
