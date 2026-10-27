using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

public class IgnoreFolderTests
{
    [Fact]
    public void Rule_IsOnlyThatFolder()
    {
        Assert.Equal("/Work/game/Library/", IgnoreFolder.Rule("Work/game/Library"));
        Assert.Equal("/Library/", IgnoreFolder.Rule(@"Library\"));
    }

    [Fact]
    public void With_AddsOnce_AndWithout_TakesOnlyThatRule()
    {
        var rules = IgnoreFolder.With(["*.bak"], "Work/game/Library");
        Assert.Equal(["*.bak", "/Work/game/Library/"], rules);
        Assert.Equal(rules, IgnoreFolder.With(rules, "work/Game/library"));
        Assert.Equal(["*.bak"], IgnoreFolder.Without(rules, "Work/game/Library"));
        Assert.Equal(["*.bak"], IgnoreFolder.Without(["*.bak", "Work/game/Library/"], "Work/game/Library")); // written by hand
    }

    [Fact]
    public void AName_IsNotTheRuleOfADeeperFolder()
    {
        Assert.False(IgnoreFolder.IsOwnRule(["Library/"], "game/Library"));
        Assert.True(IgnoreFolder.IsOwnRule(["Library/"], "Library"));
    }

    [Fact]
    public void For_AsksToIgnore_ThenToSyncAgain()
    {
        var ignore = IgnoreFolder.For([], "game/Library", isFolder: true);
        Assert.Equal(["/game/Library/"], ignore.Rules);
        Assert.Equal("Ignore", ignore.Yes);
        Assert.StartsWith("Stop syncing \"Library\"?", ignore.Text);

        var again = IgnoreFolder.For(ignore.Rules!, "game/Library", isFolder: true);
        Assert.Empty(again.Rules!);
        Assert.Equal("Sync it again", again.Yes);
    }

    [Fact]
    public void For_OnlyTellsWhenItCantChangeAnything()
    {
        Assert.Null(IgnoreFolder.For([], "a.txt", isFolder: false).Rules);
        Assert.Null(IgnoreFolder.For([], "", isFolder: true).Rules);
        var other = IgnoreFolder.For(["node_modules/"], "web/node_modules", isFolder: true);
        Assert.Null(other.Rules);
        Assert.Contains("Settings", other.Text);
    }
}
