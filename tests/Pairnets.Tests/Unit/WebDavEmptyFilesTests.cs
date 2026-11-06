using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// <see cref="DavEmptyFiles"/>: when an empty file a WebDAV link made (Finder's and Explorer's first, empty PUT) is handed to the
/// keeper to be stored, and when it never is (docs/WEBDAV.md). WebDavTests runs the same through the real server.
/// </summary>
public class WebDavEmptyFilesTests
{
    private static readonly DeviceIdentity Mac = new("dev_mac", "Mac (WebDAV link)", DeviceAuthKind.WebDavKey);
    private static readonly DeviceIdentity Pc = new("dev_pc", "PC (WebDAV link)", DeviceAuthKind.WebDavKey);

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void AHeldFileWaitsTheQuietTimeOrWhileLockedTheLockedTime()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var held = new DavEmptyFiles(clock);
        Assert.True(held.TryAdd("a.txt", Mac));
        Assert.True(held.TryAdd("b.txt", Mac));
        held.Locked("b.txt");

        clock.Advance(DavEmptyFiles.QuietTime - Second);
        Assert.Empty(held.TakeDue());
        clock.Advance(Second);
        Assert.Equal(["a.txt"], held.TakeDue().Select(f => f.Path));
        Assert.Null(held.MadeAt("a.txt")); // handed over: no longer held

        // locked: as long as the lock it was given, and a refresh starts that over
        clock.Advance(TimeSpan.FromMinutes(30));
        held.Locked("b.txt");
        clock.Advance(DavEmptyFiles.LockedTime - Second);
        Assert.Empty(held.TakeDue());
        held.Unlocked("b.txt"); // done with it, and no content came
        clock.Advance(DavEmptyFiles.QuietTime - Second);
        Assert.Empty(held.TakeDue());
        held.Touch("b.txt"); // a PROPPATCH: still busy with it
        clock.Advance(DavEmptyFiles.QuietTime - Second);
        Assert.Empty(held.TakeDue());
        clock.Advance(Second);
        var b = Assert.Single(held.TakeDue());
        Assert.Equal(("b.txt", Mac), (b.Path, b.By));

        // a locked one whose client went away for good: after the hour
        Assert.True(held.TryAdd("c.txt", Pc));
        held.Locked("c.txt");
        clock.Advance(DavEmptyFiles.LockedTime);
        Assert.Equal(["c.txt"], held.TakeDue().Select(f => f.Path));
    }

    [Fact]
    public void AHeldFileIsNeverHandedOverWhileARequestWritesIntoIt()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var held = new DavEmptyFiles(clock);
        Assert.Null(held.Writing("nothing.txt"));
        Assert.True(held.TryAdd("movie.mov", Mac));

        var writing = held.Writing("movie.mov");
        Assert.NotNull(writing);
        clock.Advance(TimeSpan.FromDays(1)); // a slow upload
        Assert.Empty(held.TakeDue());
        writing.Dispose(); // it ended without storing the file (refused for another reason, or the client went): the wait starts over
        writing.Dispose(); // counted once
        Assert.Empty(held.TakeDue());
        clock.Advance(DavEmptyFiles.QuietTime);
        Assert.Single(held.TakeDue());

        // the write stored the file (or was over 25 MB) and took it away: that request's end does not count for a new one of the name
        Assert.True(held.TryAdd("photo.jpg", Mac));
        var first = held.Writing("photo.jpg")!;
        Assert.True(held.Remove("photo.jpg"));
        Assert.True(held.TryAdd("photo.jpg", Pc));
        var second = held.Writing("photo.jpg")!;
        first.Dispose();
        clock.Advance(DavEmptyFiles.QuietTime);
        Assert.Empty(held.TakeDue()); // still being written into by the second
        second.Dispose();
        clock.Advance(DavEmptyFiles.QuietTime);
        Assert.Equal(Pc, Assert.Single(held.TakeDue()).By);
    }

    [Fact]
    public void AHeldFileKeepsWhenItWasMadeFollowsARenameAndGoesWithItsFolderOrLink()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var held = new DavEmptyFiles(clock);
        Assert.True(held.TryAdd("New/a.txt", Mac));
        var made = held.MadeAt("New/a.txt");
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(held.TryAdd("New/a.txt", Mac)); // made empty again: when it was made stays
        Assert.Equal(made, held.MadeAt("New/a.txt"));
        Assert.True(held.TryAdd("New/Sub/b.txt", Pc));
        Assert.True(held.TryAdd("Newer/c.txt", Pc));

        held.Move("New", "Trip");
        Assert.Equal(["Trip/Sub/b.txt", "Trip/a.txt"], held.Under("Trip").Select(f => f.Path));
        Assert.Equal(made, held.MadeAt("Trip/a.txt"));
        Assert.Null(held.MadeAt("New/a.txt"));
        Assert.NotNull(held.MadeAt("Newer/c.txt")); // only that folder, not one whose name starts the same
        held.Move("Trip/a.txt", "Trip/A.txt"); // a rename by letter case
        Assert.Equal(["Trip/A.txt", "Trip/Sub/b.txt"], held.Under("Trip").Select(f => f.Path));

        Assert.Equal(2, held.RemoveBy("dev_pc")); // a removed link's files go
        Assert.Equal(["Trip/A.txt"], held.Under(string.Empty).Select(f => f.Path));
        held.RemoveUnder("Trip");
        Assert.Equal(0, held.Count);
    }

    [Fact]
    public void PastTheMostHeldAnEmptyFileIsNotHeldSoItIsStoredAtOnce()
    {
        var held = new DavEmptyFiles();
        for (var i = 0; i < DavEmptyFiles.MaxHeld; i++)
            Assert.True(held.TryAdd($"f{i}.txt", Mac));
        Assert.False(held.TryAdd("one-more.txt", Mac));
        Assert.True(held.TryAdd("f0.txt", Mac)); // made empty again is no new one
        Assert.Equal(DavEmptyFiles.MaxHeld, held.Count);
    }

    [Fact]
    public void AStoppingNestHandsOverTheOnesNobodyHoldsALockOnOrWritesInto()
    {
        var held = new DavEmptyFiles();
        Assert.True(held.TryAdd("plain.txt", Mac));
        Assert.True(held.TryAdd("locked.txt", Mac));
        held.Locked("locked.txt");
        Assert.True(held.TryAdd("writing.txt", Mac));
        using var writing = held.Writing("writing.txt");

        Assert.Equal(["plain.txt"], held.TakeAllForStop().Select(f => f.Path));
        Assert.Equal(0, held.Count);
    }
}
