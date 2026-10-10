using Pairnets.Core;
using Pairnets.Core.State;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public class StateDbTests
{
    private static readonly TimeSpan Racy = TimeSpan.FromSeconds(2);

    [Fact]
    public void CacheIsTrustedOnlyWhenFileIsOlderThanTheHash()
    {
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var old = new FileState("a", null, 10, mtime, "h", mtime + TimeSpan.FromSeconds(10).Ticks);
        Assert.True(old.CacheValidFor(10, mtime, Racy));
        Assert.False(old.CacheValidFor(11, mtime, Racy));
        Assert.False(old.CacheValidFor(10, mtime + 1, Racy));

        // Hashed within the racy window of the modification: a same-size edit in the same tick
        // would be invisible, so the cache must not be trusted.
        var racy = old with { HashedAtTicks = mtime + TimeSpan.FromMilliseconds(500).Ticks };
        Assert.False(racy.CacheValidFor(10, mtime, Racy));
        Assert.False((old with { Hash = null }).CacheValidFor(10, mtime, Racy));
    }

    [Fact]
    public void PersistsBasesCacheAndMeta()
    {
        using var dir = new TempDir();
        var path = dir.Combine("state.db");
        using (var db = new StateDb(path))
        {
            db.SetBase("a.txt", "base-a");
            db.SetCache("a.txt", 5, 100, "hash-a", 200);
            db.SetCache("cache-only.txt", 1, 1, "c", 2);
            db.SetSynced("b.txt", "base-b", 7, 70, "base-b", 80);
            db.MarkerId = "marker";
            db.Cursor = 42;
            Assert.Equal(2, db.CountTracked());
        }
        using (var db = new StateDb(path))
        {
            var a = db.GetFile("a.txt")!;
            Assert.Equal("base-a", a.BaseHash);
            Assert.Equal("hash-a", a.Hash);
            Assert.Equal(5, a.Size);
            Assert.Equal("marker", db.MarkerId);
            Assert.Equal(42, db.Cursor);
            Assert.Equal(3, db.LoadFiles().Count);

            db.PruneCacheRows(new HashSet<string> { "a.txt" });
            Assert.Null(db.GetFile("cache-only.txt"));
            Assert.NotNull(db.GetFile("b.txt")); // has a base: never pruned
        }
    }

    [Fact]
    public void RemoteMirrorAppliesFullAndDeltaAndIgnoresOlderVersions()
    {
        using var dir = new TempDir();
        using var db = new StateDb(dir.Combine("s.db"));
        db.ApplyRemote([new ManifestEntry("a", "h1", 1, 1, false, 1), new ManifestEntry("b", "h2", 1, 1, false, 2)], true, 2, "srv");
        db.ApplyRemote([new ManifestEntry("a", null, 0, 1, true, 3)], false, 3, "srv");
        db.UpsertRemote(new ManifestEntry("a", "stale", 1, 1, false, 1));
        var remote = db.LoadRemote();
        Assert.True(remote["a"].Deleted);
        Assert.Equal("h2", remote["b"].Hash);
        Assert.Equal(3, db.Cursor);
        Assert.Equal("srv", db.ServerId);

        db.ApplyRemote([new ManifestEntry("c", "h3", 1, 1, false, 4)], true, 4, "srv");
        Assert.Equal(["c"], db.LoadRemote().Keys);
    }

    [Fact]
    public void ApprovalCoversExactlyOnePass()
    {
        using var dir = new TempDir();
        using var db = new StateDb(dir.Combine("s.db"));
        Assert.Null(db.ConsumeApproval());

        db.SetPendingDeletes([(DeleteSide.Local, "a"), (DeleteSide.Remote, "b")]);
        Assert.Equal(2, db.GetPendingDeletes().Count);
        Assert.Equal(2, db.ApprovePendingDeletes());
        Assert.Empty(db.GetPendingDeletes());

        var approval = db.ConsumeApproval();
        Assert.NotNull(approval);
        Assert.Contains((DeleteSide.Local, "a"), approval);
        Assert.Contains((DeleteSide.Remote, "b"), approval);
        Assert.Null(db.ConsumeApproval());
    }

    [Fact]
    public void ClearAllBasesKeepsCacheAndForgetsServer()
    {
        using var dir = new TempDir();
        using var db = new StateDb(dir.Combine("s.db"));
        db.SetSynced("a", "base", 1, 1, "base", 5);
        db.ApplyRemote([new ManifestEntry("a", "base", 1, 1, false, 1)], true, 1, "srv");
        db.ClearAllBases();
        Assert.Equal(0, db.CountTracked());
        Assert.Equal("base", db.GetFile("a")!.Hash);
        Assert.Null(db.ServerId);
        Assert.Equal(0, db.Cursor);
        Assert.Empty(db.LoadRemote());
    }

    [Fact]
    public void WarningsRoundTrip()
    {
        using var dir = new TempDir();
        using var db = new StateDb(dir.Combine("s.db"));
        db.SetWarning(new PathWarning("A.txt", "case-collision", "msg", "h", 3, 99));
        var w = db.LoadWarnings()["A.txt"];
        Assert.Equal("case-collision", w.Code);
        Assert.Equal(3, w.ServerCursor);
        db.ClearWarning("A.txt");
        Assert.Empty(db.LoadWarnings());
    }

    [Fact]
    public void UnfinishedTransfersRoundTripAndGoWithARelink()
    {
        using var dir = new TempDir();
        var path = dir.Combine("s.db");
        using (var db = new StateDb(path))
        {
            db.SetUpload(new UploadCheckpoint("v.mp4", "id1", "https://sync.example.com/", "none", 10, 20, "h", 30));
            db.SetUpload(new UploadCheckpoint("v.mp4", "id2", "https://sync.example.com/", "none", 10, 20, "h", 31));
            db.SetDownload(new PartialDownload("m.mkv", "abc.part", "https://sync.example.com/", "h2", 99, 40));
        }
        using (var db = new StateDb(path))
        {
            Assert.Equal("id2", db.GetUpload("v.mp4")!.UploadId);
            Assert.Single(db.LoadUploads());
            db.RemoveUpload("v.mp4", "id1"); // another session's: kept
            Assert.NotNull(db.GetUpload("v.mp4"));
            Assert.Equal(new PartialDownload("m.mkv", "abc.part", "https://sync.example.com/", "h2", 99, 40), db.GetDownload("m.mkv"));
            db.ClearAllBases();
            Assert.Empty(db.LoadUploads());
            Assert.Empty(db.LoadDownloads());
        }
    }

    [Fact]
    public void AStateDatabaseFromTheVersionBeforeIsUpgradedInPlace()
    {
        using var dir = new TempDir();
        var path = dir.Combine("s.db");
        // The schema as the previous release left it (user_version 1, no transfer tables).
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE files(path TEXT PRIMARY KEY NOT NULL, baseHash TEXT NULL, size INTEGER NOT NULL DEFAULT -1,
                    mtimeTicks INTEGER NOT NULL DEFAULT 0, hash TEXT NULL, hashedAtTicks INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE remote(path TEXT PRIMARY KEY NOT NULL, hash TEXT NULL, size INTEGER NOT NULL, modifiedMs INTEGER NOT NULL,
                    deleted INTEGER NOT NULL, version INTEGER NOT NULL);
                CREATE TABLE warnings(path TEXT PRIMARY KEY NOT NULL, code TEXT NOT NULL, message TEXT NULL, localHash TEXT NULL,
                    serverCursor INTEGER NOT NULL, createdMs INTEGER NOT NULL);
                CREATE TABLE pendingDeletes(side TEXT NOT NULL, path TEXT NOT NULL, PRIMARY KEY(side, path));
                CREATE TABLE approvedDeletes(side TEXT NOT NULL, path TEXT NOT NULL, PRIMARY KEY(side, path));
                CREATE TABLE meta(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
                INSERT INTO files(path, baseHash) VALUES('kept.txt', 'base');
                INSERT INTO meta(key, value) VALUES('marker', 'm1');
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = new StateDb(path);
        Assert.Equal("base", db.GetFile("kept.txt")!.BaseHash);
        Assert.Equal("m1", db.MarkerId);
        db.SetUpload(new UploadCheckpoint("a", "id", "s", "none", 1, 2, "h", 3));
        Assert.NotNull(db.GetUpload("a"));
    }
}
