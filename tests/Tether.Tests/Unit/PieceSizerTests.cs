using Tether.Core.Api;

namespace Tether.Tests.Unit;

/// <summary>How big the pieces of an upload are: sized to the connection, smaller after a cut.</summary>
public class PieceSizerTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void BeforeAnythingIsKnownOnlyBigFilesAreSplit()
    {
        var sizer = new PieceSizer(4 * MiB, 50 * MiB);
        Assert.Equal(12.5 * MiB, sizer.Next);
        Assert.False(sizer.ShouldSplit(50 * MiB));
        Assert.True(sizer.ShouldSplit(50 * MiB + 1));
    }

    [Fact]
    public void OnAFastConnectionPiecesDoubleUpToTheLargest()
    {
        var sizer = new PieceSizer(4 * MiB, 50 * MiB);
        var sizes = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            sizer.Succeeded(sizer.Next, TimeSpan.FromSeconds(1)); // far faster than 30 s per piece
            sizes.Add(sizer.Next);
        }
        Assert.Equal([25 * MiB, 50 * MiB, 50 * MiB, 50 * MiB], sizes);
        Assert.False(sizer.ShouldSplit(50 * MiB)); // nothing changes for files that fit one piece
    }

    [Fact]
    public void OnASlowConnectionAPieceTakesAboutThirtySeconds()
    {
        var sizer = new PieceSizer(4 * MiB, 50 * MiB);
        // 250 KB/s, about a 2 Mbit/s upload shared with others.
        for (var i = 0; i < 6; i++)
            sizer.Succeeded(sizer.Next, TimeSpan.FromSeconds(sizer.Next / (250.0 * 1000)));
        Assert.InRange(sizer.Next, 7_400_000, 7_600_000);
        // Then a file that would take over a minute goes in pieces too.
        Assert.True(sizer.ShouldSplit(20 * MiB));
        Assert.False(sizer.ShouldSplit(5 * MiB));
    }

    [Fact]
    public void AVerySlowConnectionStillGetsTheSmallestPiece()
    {
        var sizer = new PieceSizer(4 * MiB, 50 * MiB);
        for (var i = 0; i < 6; i++)
            sizer.Succeeded(sizer.Next, TimeSpan.FromSeconds(sizer.Next / (10.0 * 1000)));
        Assert.Equal(4 * MiB, sizer.Next);
    }

    [Fact]
    public void ACutHalvesThePiecesDownToTheSmallest()
    {
        var sizer = new PieceSizer(4 * MiB, 50 * MiB);
        sizer.Succeeded(sizer.Next, TimeSpan.FromSeconds(1));
        sizer.Succeeded(sizer.Next, TimeSpan.FromSeconds(1));
        Assert.Equal(50 * MiB, sizer.Next);
        sizer.Failed();
        Assert.Equal(25 * MiB, sizer.Next);
        for (var i = 0; i < 10; i++)
            sizer.Failed();
        Assert.Equal(4 * MiB, sizer.Next);
    }

    [Fact]
    public void ASmallestAboveTheLargestMeansFixedPieces()
    {
        var sizer = new PieceSizer(4 * MiB, 64 * 1024);
        Assert.Equal(64 * 1024, sizer.Min);
        Assert.Equal(64 * 1024, sizer.Next);
        sizer.Failed();
        sizer.Succeeded(1, TimeSpan.FromHours(1));
        Assert.Equal(64 * 1024, sizer.Next);
    }

    [Fact]
    public void NonsenseMeasurementsAreIgnored()
    {
        var sizer = new PieceSizer(4 * MiB, 50 * MiB);
        sizer.Succeeded(0, TimeSpan.FromSeconds(1));
        sizer.Succeeded(MiB, TimeSpan.Zero);
        Assert.Equal(12.5 * MiB, sizer.Next);
        Assert.False(sizer.ShouldSplit(20 * MiB)); // still "speed unknown"
    }
}
