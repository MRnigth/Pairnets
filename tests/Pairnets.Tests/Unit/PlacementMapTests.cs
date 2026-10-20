using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>"Where your files live": which folder's choice a path takes, and the decision for nest-only files.</summary>
public class PlacementMapTests
{
    private static PlacementMap Map(bool onDemandWorks = true, params (string, FilePlacement)[] folders) =>
        new(folders.Select(f => KeyValuePair.Create(f.Item1, f.Item2)), onDemandWorks);

    [Fact]
    public void NothingChosenMeansEverythingHere()
    {
        Assert.Equal(FilePlacement.OnThisPc, PlacementMap.Everywhere.For("a/b.txt"));
        Assert.False(PlacementMap.Everywhere.AnyAway);
    }

    [Fact]
    public void TheNearestFolderWins()
    {
        var map = Map(true, ("Photos", FilePlacement.NestOnly), ("Photos/Keep", FilePlacement.OnThisPc), ("", FilePlacement.OnDemand));
        Assert.Equal(FilePlacement.NestOnly, map.For("Photos/2024/beach.jpg"));
        Assert.Equal(FilePlacement.OnThisPc, map.For("Photos/Keep/me.jpg"));
        Assert.Equal(FilePlacement.OnDemand, map.For("Documents/cv.docx"));
        Assert.Equal(FilePlacement.OnDemand, map.For("top.txt"));
        Assert.Equal(FilePlacement.NestOnly, map.For("Photos"));
        Assert.True(map.AnyAway);
        Assert.True(map.Uses(FilePlacement.NestOnly));
    }

    [Fact]
    public void LetterCaseAndSlashesDoNotMatter()
    {
        var map = Map(true, ("\\photos\\", FilePlacement.NestOnly));
        Assert.Equal(FilePlacement.NestOnly, map.For("PHOTOS/a.jpg"));
        Assert.Equal(FilePlacement.OnThisPc, map.For("Photoshop/a.psd"));
    }

    [Fact]
    public void OnDemandIsHereWhereItCannotWork()
    {
        var map = Map(false, ("Music", FilePlacement.OnDemand), ("Video", FilePlacement.NestOnly));
        Assert.Equal(FilePlacement.OnThisPc, map.For("Music/song.mp3"));
        Assert.Equal(FilePlacement.NestOnly, map.For("Video/film.mkv"));
    }

    [Fact]
    public void SettingsKeepPlacementPerCopyAndThroughTheFile()
    {
        using var dir = new Infrastructure.TempDir("placement-settings");
        var path = Path.Combine(dir.Path, "settings.json");
        var settings = new ClientSettings();
        settings.Placement["Photos"] = FilePlacement.NestOnly;
        var copy = settings.Clone();
        copy.Placement["Music"] = FilePlacement.OnDemand;
        Assert.False(settings.Placement.ContainsKey("Music"));

        SettingsStore.Save(path, copy);
        var loaded = SettingsStore.Load(path);
        Assert.Equal(FilePlacement.NestOnly, loaded.Placement["Photos"]);
        Assert.Equal(FilePlacement.OnDemand, loaded.Placement["Music"]);
        Assert.Equal(FilePlacement.NestOnly, loaded.PlacementMap(onDemandWorks: true).For("Photos/x.jpg"));
    }

    [Fact]
    public void OldSettingsFilesHaveNoPlacement()
    {
        using var dir = new Infrastructure.TempDir("placement-old");
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "folder": "C:\\Pairnets", "placement": null }""");
        var loaded = SettingsStore.Load(path);
        Assert.Empty(loaded.Placement);
    }

    // l = here, s = on the nest, b = last agreed.
    [Theory]
    [InlineData(null, "S", null, SyncAction.None)] // new on the nest: stays there
    [InlineData(null, null, null, SyncAction.None)]
    [InlineData(null, "B", "B", SyncAction.DeleteRemote)] // deleted here after it was synced: a real deletion
    [InlineData(null, "S", "B", SyncAction.ClearBase)] // deleted here, changed on the nest: the nest's stays there
    [InlineData(null, null, "B", SyncAction.ClearBase)]
    [InlineData("X", "X", "X", SyncAction.Evict)] // safely on the nest: free it
    [InlineData("X", "X", null, SyncAction.RecordBase)] // the same, not yet recorded: next pass frees it
    [InlineData("L", "B", "B", SyncAction.Upload)] // edited here: upload first
    [InlineData("L", null, null, SyncAction.Upload)] // new here
    [InlineData("B", "S", "B", SyncAction.Evict)] // an untouched older copy: the newer one stays on the nest
    [InlineData("L", "S", "B", SyncAction.Conflict)]
    [InlineData("B", null, "B", SyncAction.DeleteLocal)] // deleted on the nest
    public void NestOnlyDecision(string? l, string? s, string? b, SyncAction expected)
    {
        Assert.Equal(expected, SyncEngine.NestOnly(SyncDecision.Decide(l, s, b), l, s, b));
    }
}
