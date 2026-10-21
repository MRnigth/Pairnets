using Pairnets.Core;
using Pairnets.Server;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// The server side of "Where your nest keeps files": the requests it leaves for the root helper (deploy/place.sh), how
/// often, and what it refuses before the helper is even asked.
/// </summary>
public sealed class StorageMoverTests : IDisposable
{
    /// <summary>The drives.json the root helper writes (the shape in deploy/place.sh).</summary>
    public const string SampleDrives = """
        {"current":"/var/lib/pairnets","mainDisk":"/var/lib/pairnets","byHand":false,"listedAt":"2026-10-20T12:00:00Z","drives":[
        {"mount":"/","label":"Main disk","fs":"ext4","free":182000000000,"total":250000000000,"folder":"/var/lib/pairnets","problem":"","main":true},
        {"mount":"/mnt/data","label":"data","fs":"ext4","free":4000000000000,"total":4400000000000,"folder":"/mnt/data/pairnets","problem":"","main":false},
        {"mount":"/media/stick","label":"STICK","fs":"vfat","free":12000000000,"total":32000000000,"folder":"/media/stick/pairnets","problem":"this drive has no Linux file permissions (vfat); format it as ext4 to use it","main":false}]}
        """;

    private readonly TempDir _dir = new("storage-mover");
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 10, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    private StorageMover Mover(bool installed = true)
    {
        var script = _dir.Combine("bin", "update.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "#!/bin/sh\n");
        var unit = _dir.Combine("pairnets-place.path");
        if (installed)
        {
            File.WriteAllText(_dir.Combine("bin", "place.sh"), "#!/bin/sh\n");
            File.WriteAllText(unit, "[Path]\n");
        }
        var options = new SyncOptions { UpdaterScript = script, UpdateDir = _dir.Combine("update"), DataDir = _dir.Combine("data"), PlacePathUnit = unit };
        var paths = new ServerPaths(options.DataDir);
        Directory.CreateDirectory(paths.DataDir);
        return new StorageMover(paths, options, new ServerUpdater(paths, options, _clock), _clock);
    }

    private void Drives(StorageMover mover, string json = SampleDrives)
    {
        Directory.CreateDirectory(mover.UpdateDir);
        File.WriteAllText(mover.DrivesFile, json);
    }

    private static string? Request(StorageMover mover) => File.Exists(mover.RequestFile) ? File.ReadAllText(mover.RequestFile) : null;

    [Fact]
    public void WithoutTheHelperNothingIsAskedAndAMoveIsRefused()
    {
        var mover = Mover(installed: false);
        var storage = mover.Get();
        Assert.False(storage.CanMove);
        Assert.Null(Request(mover));
        Assert.Null(storage.Status);
        Assert.Equal(StorageMover.MoveOutcome.NotInstalled, mover.RequestMove("/mnt/data/pairnets").Outcome);
    }

    [Fact]
    public void AMissingListIsAskedForOnceAndAgainOnlyWhenOldAndNotTooOften()
    {
        var mover = Mover();
        var first = mover.Get();
        Assert.True(first.CanMove);
        Assert.Empty(first.Drives);
        Assert.Equal("action=list\n", Request(mover));

        // While that request waits, no second one; once the helper took it, still not within 10 seconds.
        mover.Get();
        File.Delete(mover.RequestFile);
        _clock.Advance(TimeSpan.FromSeconds(5));
        mover.Get();
        Assert.Null(Request(mover));
        _clock.Advance(TimeSpan.FromSeconds(6));
        mover.Get();
        Assert.Equal("action=list\n", Request(mover));
        File.Delete(mover.RequestFile);

        // A fresh list (listed 25 s ago) is not asked for again; one older than 30 s is.
        Drives(mover);
        _clock.Now = new DateTimeOffset(2026, 10, 20, 12, 0, 25, TimeSpan.Zero);
        var listed = mover.Get();
        Assert.Equal(3, listed.Drives.Count);
        Assert.Equal("/var/lib/pairnets", listed.Current);
        Assert.True(listed.OnMainDisk);
        Assert.Null(Request(mover));
        _clock.Now = new DateTimeOffset(2026, 10, 20, 12, 0, 35, TimeSpan.Zero);
        mover.Get();
        Assert.Equal("action=list\n", Request(mover));
    }

    [Fact]
    public void AMoveGoesOnlyToAListedUsableDriveAndOnceAMinute()
    {
        var mover = Mover();
        Assert.Equal(StorageMover.MoveOutcome.UnknownFolder, mover.RequestMove("/mnt/data/pairnets").Outcome); // no list yet
        Drives(mover);
        Assert.Equal(StorageMover.MoveOutcome.UnknownFolder, mover.RequestMove("/etc").Outcome);
        Assert.Equal(StorageMover.MoveOutcome.UnknownFolder, mover.RequestMove("/media/stick/pairnets").Outcome); // greyed: vfat
        Assert.Equal(StorageMover.MoveOutcome.UnknownFolder, mover.RequestMove("/mnt/data/pairnets\naction=list").Outcome);
        Assert.Equal(StorageMover.MoveOutcome.UnknownFolder, mover.RequestMove("mnt/data/pairnets").Outcome);
        Assert.Equal(StorageMover.MoveOutcome.UnknownFolder, mover.RequestMove(null).Outcome);
        Assert.Equal(StorageMover.MoveOutcome.AlreadyThere, mover.RequestMove("/var/lib/pairnets/").Outcome);
        Assert.Null(Request(mover));

        Assert.Equal(StorageMover.MoveOutcome.Requested, mover.RequestMove("/mnt/data/pairnets/").Outcome);
        Assert.Equal("action=move\ndata_dir=/mnt/data/pairnets\n", Request(mover));
        File.SetLastWriteTimeUtc(mover.RequestFile, _clock.GetUtcNow().UtcDateTime);
        var waiting = mover.Get();
        Assert.Equal(StorageMoveStatus.Requested, waiting.Status!.State);
        Assert.Equal(StorageMover.MoveOutcome.Busy, mover.RequestMove("/mnt/data/pairnets").Outcome); // the request still waits

        // The helper took it and failed at once: a new ask within the minute is too soon.
        File.Delete(mover.RequestFile);
        File.WriteAllText(mover.StatusFile, """{"state":"failed","percent":0,"message":"That drive can't be used: x. Nothing was changed.","oldCopy":"","at":"2026-10-20T12:00:11Z"}""");
        Assert.Equal(StorageMover.MoveOutcome.TooSoon, mover.RequestMove("/mnt/data/pairnets").Outcome);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(StorageMover.MoveOutcome.Requested, mover.RequestMove("/mnt/data/pairnets").Outcome);
    }

    [Fact]
    public void AMoveIsRefusedWhileOneRunsAndWhenTheMountWasMadeByHand()
    {
        var mover = Mover();
        Drives(mover);
        _clock.Now = new DateTimeOffset(2026, 10, 20, 12, 1, 0, TimeSpan.Zero); // the list is old, but the helper is busy moving
        File.WriteAllText(mover.StatusFile, """{"state":"running","percent":40,"message":"Copying your files to the new drive","oldCopy":"","at":"2026-10-20T12:00:05Z"}""");
        File.SetLastWriteTimeUtc(mover.StatusFile, _clock.GetUtcNow().UtcDateTime);
        var running = mover.Get();
        Assert.Equal(StorageMoveStatus.Running, running.Status!.State);
        Assert.Equal(40, running.Status.Percent);
        Assert.Null(Request(mover)); // no list request while a move runs (the helper is busy)
        Assert.Equal(StorageMover.MoveOutcome.Busy, mover.RequestMove("/mnt/data/pairnets").Outcome);

        // A "running" nobody has written for half an hour is a move that ended without a word.
        _clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(StorageMoveStatus.Failed, mover.Get().Status!.State);
        File.Delete(mover.StatusFile);

        Drives(mover, SampleDrives.Replace("\"byHand\":false", "\"byHand\":true", StringComparison.Ordinal));
        Assert.True(mover.Get().ByHand);
        Assert.Equal(StorageMover.MoveOutcome.ByHand, mover.RequestMove("/mnt/data/pairnets").Outcome);
    }

    [Fact]
    public void AMoveNobodyPicksUpIsReportedAfterAWhile()
    {
        var mover = Mover();
        Drives(mover);
        Assert.Equal(StorageMover.MoveOutcome.Requested, mover.RequestMove("/mnt/data/pairnets").Outcome);
        File.SetLastWriteTimeUtc(mover.RequestFile, _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(StorageMoveStatus.Requested, mover.Get().Status!.State);
        _clock.Advance(TimeSpan.FromSeconds(46));
        var stuck = mover.Get().Status!;
        Assert.Equal(StorageMoveStatus.Failed, stuck.State);
        Assert.Contains("pairnets-place.path", stuck.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelpersFilesParseAsTheAppsSeeThem()
    {
        var drives = System.Text.Json.JsonSerializer.Deserialize<StorageDrivesFile>(SampleDrives, PairnetsJson.Options)!;
        Assert.Equal("/var/lib/pairnets", drives.Current);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), drives.ListedAt);
        Assert.Equal(3, drives.Drives!.Count);
        Assert.True(drives.Drives[0].Main && drives.Drives[0].Usable);
        Assert.Equal(4000000000000, drives.Drives[1].Free);
        Assert.False(drives.Drives[2].Usable);

        var status = System.Text.Json.JsonSerializer.Deserialize<StorageMoveStatus>(
            """{"state":"succeeded","percent":100,"message":"Done.","oldCopy":"/var/lib/pairnets-before-move-2026-10-20-120000","at":"2026-10-20T12:05:00Z"}""",
            PairnetsJson.Options)!;
        Assert.Equal(StorageMoveStatus.Succeeded, status.State);
        Assert.False(status.IsBusy);
        Assert.Equal("/var/lib/pairnets-before-move-2026-10-20-120000", status.OldCopy);
    }
}
