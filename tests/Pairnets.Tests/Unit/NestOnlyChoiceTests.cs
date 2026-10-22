using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>
/// "Where your files live": the whole Pairnets folder is never nest only, a choice for one folder touches only that folder,
/// the question names what a choice changes, and the card and the Overview say what happened (docs/DECISIONS.md).
/// </summary>
public class NestOnlyChoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 10, 40, 0, TimeSpan.Zero);

    private static NestEntry Folder(string path, int files, FilePlacement placement = FilePlacement.OnThisPc) =>
        new(path, true, files * 1000L, Now, null, FileWhere.Here, placement, files);

    private static NestEntry Loose(string path) => new(path, false, 1000, Now, "h", FileWhere.Here, FilePlacement.OnThisPc);

    // ------------------------------------------------------------------ the whole folder is never nest only

    [Fact]
    public void TheWholeFolder_IsNeverNestOnly_ButOneFolderIs()
    {
        Assert.False(PlacementMap.Allowed("", FilePlacement.NestOnly));
        Assert.False(PlacementMap.Allowed("/", FilePlacement.NestOnly));
        Assert.True(PlacementMap.Allowed("", FilePlacement.OnDemand));
        Assert.True(PlacementMap.Allowed("DCIM", FilePlacement.NestOnly));

        // An older choice for the whole folder is ignored: every file is on this computer.
        var map = new PlacementMap([new("", FilePlacement.NestOnly), new("DCIM", FilePlacement.NestOnly)]);
        Assert.Equal(FilePlacement.OnThisPc, map.For("Skærmbillede 2026-04-18 113434.png"));
        Assert.Equal(FilePlacement.OnThisPc, map.For("Screenshots/a.png"));
        Assert.Equal(FilePlacement.NestOnly, map.For("DCIM/104_PANA/P1040754.JPG"));
    }

    [Fact]
    public void TheChoicesOffered_LeaveOutNestOnly_ForTheWholeFolderOnly()
    {
        Assert.DoesNotContain(FilePlacement.NestOnly, NestFiles.ChoicesFor(""));
        Assert.Contains(FilePlacement.OnThisPc, NestFiles.ChoicesFor(""));
        Assert.Equal(NestFiles.Choices, NestFiles.ChoicesFor("DCIM"));
        if (OperatingSystem.IsWindows())
            Assert.Contains(FilePlacement.OnDemand, NestFiles.ChoicesFor("")); // the files stay visible in Explorer
    }

    [Fact]
    public void ChoosingForOneFolder_TouchesOnlyThatFolder_NeverTheWholeFolder()
    {
        var placement = new Dictionary<string, FilePlacement>(StringComparer.Ordinal);
        PlacementMap.Choose(placement, "DCIM", FilePlacement.NestOnly);
        Assert.Equal(["DCIM"], placement.Keys);

        PlacementMap.Choose(placement, "Photos/2024", FilePlacement.OnDemand);
        PlacementMap.Choose(placement, "/Videos/", FilePlacement.NestOnly);
        Assert.DoesNotContain("", placement.Keys);
        Assert.Equal(FilePlacement.NestOnly, placement["Videos"]);

        // The whole folder asked for nest only: nothing is written for it.
        PlacementMap.Choose(placement, "", FilePlacement.NestOnly);
        Assert.DoesNotContain("", placement.Keys);
        Assert.Equal(3, placement.Count);
    }

    [Fact]
    public void AnOlderSettingsFile_LosesNestOnlyForTheWholeFolder_ForEveryNest_Once()
    {
        var settings = new ClientSettings { SettingsVersion = 1 };
        settings.Placement[""] = FilePlacement.NestOnly;
        settings.Placement["DCIM"] = FilePlacement.NestOnly;
        var other = new NestProfile();
        other.Placement[""] = FilePlacement.NestOnly;
        settings.OtherNests.Add(other);

        var done = SettingsUpgrades.Apply(settings);

        Assert.Equal(["DCIM"], settings.Placement.Keys);
        Assert.Empty(other.Placement);
        Assert.Equal(SettingsUpgrades.Current, settings.SettingsVersion);
        Assert.Contains(done, d => d.Contains("whole folder", StringComparison.Ordinal));
        Assert.Empty(SettingsUpgrades.Apply(settings));

        // A whole-folder On-Demand choice stays.
        var demand = new ClientSettings { SettingsVersion = 1 };
        demand.Placement[""] = FilePlacement.OnDemand;
        SettingsUpgrades.Apply(demand);
        Assert.Equal(FilePlacement.OnDemand, demand.Placement[""]);
    }

    // ------------------------------------------------------------------ the question names what changes

    [Fact]
    public void TheQuestion_ForTheWholeFolder_NamesTheLooseFilesAndEveryFolder()
    {
        // A folder with many loose screenshots plus a camera folder.
        var top = Enumerable.Range(0, 932).Select(i => Loose($"Skærmbillede {i}.png")).Prepend(Folder("DCIM", 141)).ToList();
        Assert.Equal("This changes: 932 files right in your Pairnets folder (not in a folder), DCIM (141 files).",
            FilesPageText.Affects(top, "", FilePlacement.OnThisPc));
    }

    [Fact]
    public void TheQuestion_LeavesOutFoldersWithTheirOwnChoice_AndCapsTheList()
    {
        List<NestEntry> inFolder =
        [
            Folder("A", 1), Folder("B", 2), Folder("C", 3), Folder("D", 4), Folder("E", 5), Folder("F", 6),
            Folder("Kept", 9, FilePlacement.NestOnly),
            Loose("Photos/one.jpg"),
        ];
        Assert.Equal("This changes: 1 file right in Photos, A (1 file), B (2 files), C (3 files), D (4 files) and 2 more folders.",
            FilesPageText.Affects(inFolder, "Photos", FilePlacement.OnThisPc));
        Assert.Equal("This changes: A (1 file), B (2 files), C (3 files), D (4 files), E (5 files) and 1 more folder.",
            FilesPageText.Affects(inFolder.Where(e => e.IsFolder).ToList(), "Photos", FilePlacement.OnThisPc, max: 5));
        Assert.Equal("There are no files on your nest in Empty yet. New files you put in it follow this choice.",
            FilesPageText.Affects([], "Empty", FilePlacement.OnThisPc));
    }

    [Fact]
    public void TheNestOnlyQuestion_SaysWhatChanges_First_AndThatNewFilesLeaveToo()
    {
        var q = FilesPageText.AwayQuestion("DCIM", FilePlacement.NestOnly, 500_000_000, "This changes: 141 files right in DCIM.");
        Assert.Equal("Keep DCIM only on your nest?", q.Title);
        Assert.Equal("This changes: 141 files right in DCIM.", q.Points[0]);
        Assert.Contains("New files you put in DCIM later are removed from this PC too, once your nest has them.", q.Points);

        var demand = FilesPageText.AwayQuestion("", FilePlacement.OnDemand, 0, "This changes: DCIM (141 files).");
        Assert.Equal("This changes: DCIM (141 files).", demand.Points[0]);
        Assert.DoesNotContain(FilesPageText.AwayQuestion("DCIM", FilePlacement.NestOnly, 0).Points, p => p.StartsWith("This changes", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWholeFolderRow_SaysItIsTheWholeFolder()
    {
        Assert.Equal("All other files", FilesPageText.EverythingElse);
        Assert.Equal("Files right in your Pairnets folder and every folder not listed below", FilesPageText.EverythingElseNote);
    }

    // ------------------------------------------------------------------ the card says what happened

    private static StatusSnapshot Status(RunnerStatus status, DateTimeOffset? lastSync) =>
        StatusSnapshot.Initial with { Status = status, Text = status.ToString(), LastSyncAt = lastSync };

    [Fact]
    public void TheLineUnderTheCard_FollowsTheSync()
    {
        var since = Now.AddSeconds(-5);
        Assert.Equal("Saved: All other files → On this PC.", FilesPageText.PlacementStatus("", FilePlacement.OnThisPc, null, since, Now));
        Assert.Equal("Saved: All other files → On this PC. Starting…",
            FilesPageText.PlacementStatus("", FilePlacement.OnThisPc, StatusSnapshot.Initial, since, Now));
        Assert.Equal("Saved: All other files → On this PC. Your nest can't be reached right now; the files move once it's back.",
            FilesPageText.PlacementStatus("", FilePlacement.OnThisPc, StatusSnapshot.Initial, since, Now.AddMinutes(1)));

        var downloading = Status(RunnerStatus.Syncing, Now.AddMinutes(-20)) with
        {
            CurrentPath = "DCIM/104_PANA/P1040263.JPG", Operation = "download", FilesTotal = 149, FilesDone = 37,
            Active = [new ActiveTransfer("DCIM/104_PANA/P1040263.JPG", "download", 10, 100)],
        };
        var line = FilesPageText.PlacementStatus("", FilePlacement.OnThisPc, downloading, since, Now);
        Assert.StartsWith("Saved: All other files → On this PC. Downloading", line);
        Assert.Contains("37 of 149 files", line);

        // The folder-empty guard stopped the sync, so nothing downloads until the user chooses.
        var blocked = Status(RunnerStatus.Blocked, Now.AddMinutes(-20)) with { BlockReason = BlockReason.FolderEmpty, PendingDeletes = 1 };
        Assert.Equal("Saved: All other files → On this PC. Syncing is paused until you decide on the Overview, so no file moves yet.",
            FilesPageText.PlacementStatus("", FilePlacement.OnThisPc, blocked, since, Now));

        Assert.Equal("Saved: All other files → On this PC. Done: every file is on this PC.",
            FilesPageText.PlacementStatus("", FilePlacement.OnThisPc, Status(RunnerStatus.Idle, Now), since, Now));
        Assert.Equal("Saved: DCIM → Only on the nest. Done: its files are on your nest only. New files you put in it leave this PC once your nest has them.",
            FilesPageText.PlacementStatus("DCIM", FilePlacement.NestOnly, Status(RunnerStatus.Idle, Now), since, Now));
        // A sync that ended before the choice says nothing about it yet.
        Assert.EndsWith("Starting…", FilesPageText.PlacementStatus("DCIM", FilePlacement.NestOnly, Status(RunnerStatus.Idle, since.AddSeconds(-1)), since, Now));
    }

    [Fact]
    public void FreeingFiles_IsSaid_AndTheOverviewListsTheNestOnlyFolders()
    {
        var (title, text) = FilesPageText.FreedNotice(147);
        Assert.Equal("Moved to your nest only", title);
        Assert.StartsWith("147 files are now on your nest only, not on this PC.", text);
        Assert.StartsWith("1 file is now", FilesPageText.FreedNotice(1).Text);

        Assert.Null(OverviewModel.NestOnlyFact(StatusSnapshot.Initial));
        var fact = OverviewModel.NestOnlyFact(StatusSnapshot.Initial with { NestOnlyFolders = ["DCIM", "Photos", "Videos", "Work"] })!;
        Assert.Equal("On your nest only", fact.Label);
        Assert.Equal("DCIM, Photos, Videos and 1 more", fact.Value);
        Assert.Equal(OverviewAction.OpenFiles, fact.LinkAction);

        var upToDate = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = Now.AddMinutes(-1), Devices = [],
            Server = new ServerInfo("id", 1, 1, "1.0.61", 1L << 30, 1L << 40), NestOnlyFolders = ["DCIM"],
        };
        var model = OverviewModel.Build(upToDate, "PC", @"C:\P", 0, [], Now);
        Assert.Contains(model.Facts, f => f.Label == "On your nest only" && f.Value == "DCIM");
    }
}
