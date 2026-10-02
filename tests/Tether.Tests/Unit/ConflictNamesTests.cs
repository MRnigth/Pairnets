using Tether.Core.Paths;
using Tether.Core.Sync;

namespace Tether.Tests.Unit;

public class ConflictNamesTests
{
    private static readonly DateTime When = new(2026, 10, 2, 14, 5, 9);

    [Theory]
    [InlineData("report.docx", "report (conflict DESKTOP 2026-10-02 140509).docx")]
    [InlineData("dir/sub/notes.txt", "dir/sub/notes (conflict DESKTOP 2026-10-02 140509).txt")]
    [InlineData("Makefile", "Makefile (conflict DESKTOP 2026-10-02 140509)")]
    [InlineData(".gitignore", ".gitignore (conflict DESKTOP 2026-10-02 140509)")]
    [InlineData("archive.tar.gz", "archive.tar (conflict DESKTOP 2026-10-02 140509).gz")]
    public void BuildsReadableNames(string path, string expected) =>
        Assert.Equal(expected, ConflictNames.Make(path, "DESKTOP", When, _ => false));

    [Fact]
    public void AddsCounterWhenTaken()
    {
        var taken = new HashSet<string> { "a (conflict PC 2026-10-02 140509).txt", "a (conflict PC 2026-10-02 140509 2).txt" };
        Assert.Equal("a (conflict PC 2026-10-02 140509 3).txt", ConflictNames.Make("a.txt", "PC", When, taken.Contains));
    }

    [Fact]
    public void SanitizesDeviceNames()
    {
        Assert.Equal("my_pc_", ConflictNames.SanitizeDevice("my:pc?"));
        Assert.Equal("device", ConflictNames.SanitizeDevice("  "));
        Assert.Equal("device", ConflictNames.SanitizeDevice(null));
        var name = ConflictNames.Make("x.txt", "bad/dev\\ice*", When, _ => false);
        Assert.True(PathRules.IsValid(name), name);
    }

    [Fact]
    public void StaysWithinTheLengthLimit()
    {
        var path = new string('d', 500) + "/" + new string('f', 520) + ".txt";
        Assert.True(path.Length > 1000);
        var name = ConflictNames.Make(path, "PC", When, _ => false);
        Assert.True(name.Length <= PathRules.MaxPathLength);
        Assert.True(PathRules.IsValid(name));
        Assert.EndsWith("(conflict PC 2026-10-02 140509).txt", name);
    }
}
