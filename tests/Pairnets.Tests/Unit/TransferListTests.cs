using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Unit;

/// <summary>The sync's file list (done, moving, waiting) and the "In progress" rows built from it.</summary>
public class TransferListTests
{
    private static StatusSnapshot Syncing(params BatchFile[] batch) =>
        StatusSnapshot.Initial with { Status = RunnerStatus.Syncing, Text = "Syncing", Batch = batch };

    private static BatchFile Done(string path, string operation = "upload") => new(path, operation, BatchFileState.Done);

    private static BatchFile Moving(string path, int? percent, string operation = "upload") => new(path, operation, BatchFileState.Moving, percent);

    private static BatchFile Waiting(string path, string operation = "upload") => new(path, operation, BatchFileState.Waiting);

    // ------------------------------------------------------------------ the file list

    [Fact]
    public void PlannedFilesWaitThenMoveThenAreDone()
    {
        var batch = new SyncBatch();
        batch.Plan([("Photos/a.jpg", "upload"), ("Photos/b.jpg", "upload"), ("notes.txt", "download")]);
        Assert.Equal(["Photos/a.jpg", "Photos/b.jpg", "notes.txt"], batch.View.Select(f => f.Path));
        Assert.All(batch.View, f => Assert.True(f.IsWaiting));
        Assert.Equal("Waiting", batch.View[0].StateText);

        batch.Moving("Photos/a.jpg", "upload", 40);
        Assert.Equal(new BatchFile("Photos/a.jpg", "upload", BatchFileState.Moving, 40), batch.View[0]);
        Assert.Equal("40%", batch.View[0].StateText);
        batch.Done("Photos/a.jpg", "upload");
        Assert.Equal("Uploaded", batch.View[0].StateText);
        Assert.Null(batch.View[0].Percent);

        batch.Moving("notes.txt", "download", null);
        Assert.Equal("Downloading", batch.View[2].StateText);
        batch.Stopped("notes.txt"); // skipped or failed: it waits for the next pass
        Assert.True(batch.View[2].IsWaiting);
        batch.Stopped("Photos/a.jpg"); // a synced file stays done
        Assert.True(batch.View[0].IsDone);
    }

    [Fact]
    public void TheListIsOnlyRebuiltWhenSomethingChanged()
    {
        var batch = new SyncBatch();
        batch.Plan([("a", "upload")]);
        batch.Moving("a", "upload", 10);
        var shown = batch.View;
        batch.Moving("a", "upload", 10); // the same percent again
        Assert.Same(shown, batch.View);
        batch.Moving("a", "upload", 11);
        Assert.NotSame(shown, batch.View);
    }

    [Fact]
    public void FilesThePassDidNotPlanAreAddedWhenTheyMove()
    {
        var batch = new SyncBatch();
        batch.Plan([("a.txt", "upload")]);
        batch.Moving("a (conflict PC 2026-10-06 120000).txt", "upload", 0); // a conflict's copy
        batch.Done("b.txt", "download");
        Assert.Equal(["a.txt", "a (conflict PC 2026-10-06 120000).txt", "b.txt"], batch.View.Select(f => f.Path));
        Assert.Equal([BatchFileState.Waiting, BatchFileState.Moving, BatchFileState.Done], batch.View.Select(f => f.State));
    }

    [Fact]
    public void ANextPassOfTheSameSyncAddsToTheList()
    {
        var batch = new SyncBatch();
        batch.Plan([("a", "upload"), ("b", "upload")]);
        batch.Done("a", "upload");
        batch.Moving("b", "upload", 50);

        batch.StopMoving(); // the next pass starts
        batch.Plan([("a", "upload"), ("c", "download")]); // "a" changed again
        Assert.Equal(["a", "b", "c"], batch.View.Select(f => f.Path));
        Assert.All(batch.View, f => Assert.True(f.IsWaiting));

        batch.Clear(); // a new sync
        Assert.Empty(batch.View);
        Assert.Equal(0, batch.Count);
    }

    [Fact]
    public void AHugeSyncShowsAWindowAroundTheFilesMovingNow()
    {
        var batch = new SyncBatch(cap: 10);
        batch.Plan(Enumerable.Range(0, 30).Select(i => ($"f{i:00}", "upload")));
        for (var i = 0; i < 15; i++)
            batch.Done($"f{i:00}", "upload");
        batch.Moving("f16", "upload", 30);
        batch.Moving("f28", "upload", 60); // far past the window, still shown

        var view = batch.View;
        Assert.Equal(10, view.Count);
        Assert.Equal("f14", view[0].Path); // one done file before the first unfinished one (a tenth of the cap)
        Assert.Equal("f22", view[^2].Path);
        Assert.Equal(("f28", 60), (view[^1].Path, view[^1].Percent));
        Assert.Contains(view, f => f.Path == "f16" && f.IsMoving);
        Assert.Equal(30, batch.Count);

        // Near the end the window is the last files.
        for (var i = 15; i < 30; i++)
            batch.Done($"f{i:00}", "upload");
        Assert.Equal(Enumerable.Range(20, 10).Select(i => $"f{i:00}"), batch.View.Select(f => f.Path));
    }

    // ------------------------------------------------------------------ the "In progress" rows

    [Fact]
    public void FilesOfOneFolderBecomeOneRow()
    {
        var rows = TransferGroups.Build(Syncing(
            Done("Photos/summer/a.jpg"), Moving("Photos/summer/b.jpg", 50), Waiting("Photos/summer/c.jpg"),
            Waiting("Docs/report.pdf", "download"),
            Waiting("notes.txt")));

        Assert.Equal(["Photos/summer", "Docs/report.pdf", "notes.txt"], rows.Select(r => r.Key));
        var folder = rows[0];
        Assert.True(folder.IsFolder);
        Assert.Equal(("Photos/summer", "Photos/summer", "1 of 3 files"), (folder.Title, folder.Folder, folder.Detail));
        Assert.Equal(50, folder.Percent); // (100 + 50 + 0) / 3
        Assert.Equal("50%", folder.PercentText);
        Assert.True(folder.IsUpload);
        Assert.Equal(3, folder.Files.Count); // opens to every file of the folder, the done one too

        var single = rows[1];
        Assert.False(single.IsFolder);
        Assert.Equal(("report.pdf", "Docs", "Docs"), (single.Title, single.Folder, single.Detail));
        Assert.False(single.IsUpload);
        Assert.Equal(0, single.Percent);
        var top = rows[2];
        Assert.Equal(("notes.txt", (string?)null, ""), (top.Title, top.Folder, top.Detail));
    }

    [Fact]
    public void TopLevelFilesStaySingleRowsAndDoneOnesDropOut()
    {
        var rows = TransferGroups.Build(Syncing(Done("a.txt"), Moving("b.txt", 20), Waiting("c.txt"),
            Done("Old/1.txt"), Done("Old/2.txt")));

        Assert.Equal(["b.txt", "c.txt"], rows.Select(r => r.Key)); // "Old" is all done
        Assert.All(rows, r => Assert.False(r.IsFolder));
        Assert.Equal(20, rows[0].Percent);
    }

    [Fact]
    public void RowsWithSomethingMovingComeFirst()
    {
        var rows = TransferGroups.Build(Syncing(
            Waiting("A/1"), Waiting("A/2"),
            Waiting("B/1"), Moving("B/2", 10),
            Waiting("c.txt"),
            Moving("d.txt", 70)));

        Assert.Equal(["B", "d.txt", "A", "c.txt"], rows.Select(r => r.Key));
    }

    [Fact]
    public void MostlyDownloadsMakeADownloadRow()
    {
        var rows = TransferGroups.Build(Syncing(Waiting("A/1", "download"), Waiting("A/2", "download"), Waiting("A/3")));
        Assert.False(Assert.Single(rows).IsUpload);
        rows = TransferGroups.Build(Syncing(Waiting("A/1", "download"), Waiting("A/2")));
        Assert.True(Assert.Single(rows).IsUpload); // a tie counts as uploading
    }

    [Fact]
    public void WithoutTheFileListTheFilesMovingNowAreShown()
    {
        var s = StatusSnapshot.Initial with
        {
            Status = RunnerStatus.Syncing,
            Active = [new ActiveTransfer("Music/a.mp3", "download", 50, 100), new ActiveTransfer("Music/b.mp3", "download", 0, 0), new ActiveTransfer("x.txt", "upload", 3, 4)],
        };

        var rows = TransferGroups.Build(s);

        Assert.Equal(["Music", "x.txt"], rows.Select(r => r.Key));
        Assert.Equal(("0 of 2 files", 25), (rows[0].Detail, rows[0].Percent)); // (50 + no size yet) / 2
        Assert.Equal(75, rows[1].Percent);
        Assert.Empty(TransferGroups.Build(StatusSnapshot.Initial));
    }

    [Fact]
    public void AtMostTheAskedNumberOfRows()
    {
        var s = Syncing(Enumerable.Range(0, 6).Select(i => Waiting($"F{i}/a.txt")).ToArray());
        Assert.Equal(4, TransferGroups.Build(s).Count);
        Assert.Equal(["F0/a.txt", "F1/a.txt"], TransferGroups.Build(s, max: 2).Select(r => r.Key));
    }
}
