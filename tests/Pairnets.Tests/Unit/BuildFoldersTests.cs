using Pairnets.Core.Paths;

namespace Pairnets.Tests.Unit;

public class BuildFoldersTests
{
    private static IgnoreList With(params string[] seen)
    {
        var build = new BuildFolders();
        foreach (var path in seen)
            build.Learn(path);
        return new IgnoreList { Build = build };
    }

    [Fact]
    public void Unity_SkipsItsRebuiltFolders_OnlyInsideTheProject()
    {
        var list = With("Work/game/ProjectSettings/ProjectVersion.txt");
        Assert.True(list.IsIgnored("Work/game/Library/PackageCache/a.cs"));
        Assert.True(list.IsIgnoredDirectory("Work/game/Temp"));
        Assert.True(list.IsIgnored("work/Game/logs/Editor.log")); // letter case doesn't matter
        Assert.True(list.IsIgnoredDirectory("Work/game/UserSettings"));
        Assert.False(list.IsIgnored("Work/game/Assets/Library/x.cs")); // deeper: the project's own files
        Assert.False(list.IsIgnored("Work/game/ProjectSettings/ProjectVersion.txt"));
        Assert.False(list.IsIgnored("Books/Library/novel.txt"));
        Assert.False(list.IsIgnored("Work/game-copy/Library/a.cs"));
    }

    [Fact]
    public void AProjectAtTheTop_Counts()
    {
        var list = With("ProjectSettings/ProjectVersion.txt");
        Assert.True(list.IsIgnored("Library/a.asset"));
        Assert.False(list.IsIgnored("Assets/a.cs"));
    }

    [Fact]
    public void UnrealAndNode()
    {
        var list = With("Games/Shooter/Shooter.uproject", "site/package.json");
        Assert.True(list.IsIgnored("Games/Shooter/Intermediate/Build/x.obj"));
        Assert.True(list.IsIgnored("Games/Shooter/DerivedDataCache/a.ddp"));
        Assert.True(list.IsIgnored("Games/Shooter/Saved/Logs/a.log"));
        Assert.True(list.IsIgnored("Games/Shooter/Binaries/Win64/Game.exe"));
        Assert.False(list.IsIgnored("Games/Shooter/Content/Map.umap"));
        Assert.True(list.IsIgnored("site/node_modules/react/index.js"));
        Assert.False(list.IsIgnored("site/src/node.js"));
    }

    [Fact]
    public void WithoutAProjectFile_NothingIsSkipped()
    {
        var list = With("Work/notes.txt", ".uproject", "Work/ProjectVersion.txt");
        Assert.False(list.IsIgnored("Work/Library/a.txt"));
        Assert.False(list.IsIgnored("Library/a.txt"));
        Assert.False(list.IsIgnored("Work/node_modules/a.js"));
    }

    [Fact]
    public void Clear_ForgetsProjects()
    {
        var list = With("ProjectSettings/ProjectVersion.txt");
        list.Build!.Clear();
        Assert.False(list.IsIgnored("Library/a.asset"));
    }
}
