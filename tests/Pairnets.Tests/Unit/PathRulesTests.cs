using Pairnets.Core.Paths;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public class PathRulesTests
{
    [Theory]
    [InlineData("a.txt")]
    [InlineData("folder/sub/file.txt")]
    [InlineData("Ünïcødé/日本語/файл.txt")]
    [InlineData("emoji 😀/x.md")]
    [InlineData(".gitignore")]
    [InlineData("sub/.pairnets-marker")]
    [InlineData("name with spaces inside.txt")]
    [InlineData("a.b.c")]
    [InlineData("CONSOLE.txt")]
    [InlineData("COM10")]
    [InlineData("LPT0.txt")]
    [InlineData("nul_file")]
    [InlineData("auxiliary/x")]
    [InlineData("~$draft.docx")]
    public void AcceptsSafePaths(string path)
    {
        Assert.Equal(PathProblem.None, PathRules.Check(path));
        Assert.True(PathRules.IsValid(path));
    }

    [Theory]
    [InlineData("", PathProblem.Empty)]
    [InlineData("/abs/file", PathProblem.Rooted)]
    [InlineData("C:/x.txt", PathProblem.DriveLetter)]
    [InlineData("c:x", PathProblem.DriveLetter)]
    [InlineData("a\\b.txt", PathProblem.Backslash)]
    [InlineData("a//b", PathProblem.EmptySegment)]
    [InlineData("a/", PathProblem.EmptySegment)]
    [InlineData("./a", PathProblem.DotSegment)]
    [InlineData("a/./b", PathProblem.DotSegment)]
    [InlineData("../etc/passwd", PathProblem.DotSegment)]
    [InlineData("a/../../b", PathProblem.DotSegment)]
    [InlineData("a\0b", PathProblem.ControlCharacter)]
    [InlineData("tab\there", PathProblem.ControlCharacter)]
    [InlineData("bell\u0007", PathProblem.ControlCharacter)]
    [InlineData("del\u007f", PathProblem.ControlCharacter)]
    [InlineData("a<b", PathProblem.InvalidCharacter)]
    [InlineData("a>b", PathProblem.InvalidCharacter)]
    [InlineData("dir/a:b", PathProblem.InvalidCharacter)]
    [InlineData("say \"hi\"", PathProblem.InvalidCharacter)]
    [InlineData("a|b", PathProblem.InvalidCharacter)]
    [InlineData("why?", PathProblem.InvalidCharacter)]
    [InlineData("star*", PathProblem.InvalidCharacter)]
    [InlineData(" lead.txt", PathProblem.LeadingOrTrailingSpace)]
    [InlineData("trail.txt ", PathProblem.LeadingOrTrailingSpace)]
    [InlineData("dir /x", PathProblem.LeadingOrTrailingSpace)]
    [InlineData("trail.", PathProblem.TrailingDot)]
    [InlineData("dir./x", PathProblem.TrailingDot)]
    [InlineData("CON", PathProblem.ReservedName)]
    [InlineData("con.txt", PathProblem.ReservedName)]
    [InlineData("dir/PRN.tar.gz", PathProblem.ReservedName)]
    [InlineData("AUX", PathProblem.ReservedName)]
    [InlineData("nul.json", PathProblem.ReservedName)]
    [InlineData("COM1", PathProblem.ReservedName)]
    [InlineData("com9.log", PathProblem.ReservedName)]
    [InlineData("LPT1", PathProblem.ReservedName)]
    [InlineData("lpt9.txt", PathProblem.ReservedName)]
    [InlineData("COM\u00B9", PathProblem.ReservedName)]
    [InlineData("CON .txt", PathProblem.ReservedName)]
    [InlineData("CONIN$", PathProblem.ReservedName)]
    [InlineData(".pairnets-tmp/x.part", PathProblem.ReservedName)]
    [InlineData("a/.PAIRNETS-TMP", PathProblem.ReservedName)]
    [InlineData(".pairnets-marker", PathProblem.ReservedName)]
    public void RejectsUnsafePaths(string path, PathProblem expected)
    {
        Assert.Equal(expected, PathRules.Check(path));
        Assert.False(PathRules.IsValid(path));
    }

    [Fact]
    public void RejectsNull() => Assert.Equal(PathProblem.Empty, PathRules.Check(null));

    [Fact]
    public void RejectsUnpairedSurrogates()
    {
        Assert.Equal(PathProblem.InvalidCharacter, PathRules.Check("bad\uD800.txt"));
        Assert.Equal(PathProblem.InvalidCharacter, PathRules.Check("bad\uDC00.txt"));
    }

    [Fact]
    public void LengthLimitIs1024()
    {
        var ok = string.Join('/', Enumerable.Repeat(new string('a', 99), 10)) + "/" + new string('b', 1024 - 1000);
        Assert.Equal(1024, ok.Length);
        Assert.True(PathRules.IsValid(ok));
        Assert.Equal(PathProblem.TooLong, PathRules.Check(ok + "c"));
    }

    [Fact]
    public void ResolveUnderStaysInsideRoot()
    {
        using var root = new TempDir();
        var resolved = PathRules.ResolveUnder(root.Path, "a/b.txt");
        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(root.Path), resolved);
        Assert.Null(PathRules.ResolveUnder(root.Path, "../x"));
        Assert.Null(PathRules.ResolveUnder(root.Path, "/etc/passwd"));
    }

    [Fact]
    public void DetectsSymlinksAlongThePath()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "x");
        Links.Folder(Path.Combine(root.Path, "link"), outside.Path);
        Directory.CreateDirectory(Path.Combine(root.Path, "real"));

        Assert.True(PathRules.HasReparsePoint(root.Path, "link/secret.txt"));
        Assert.True(PathRules.HasReparsePoint(root.Path, "link"));
        Assert.False(PathRules.HasReparsePoint(root.Path, "real/new.txt"));
        Assert.False(PathRules.HasReparsePoint(root.Path, "missing/new.txt"));
    }

    [Fact]
    public void CaseKeyFoldsCase() => Assert.Equal(PathRules.CaseKey("Docs/Ä.TXT"), PathRules.CaseKey("docs/ä.txt"));

    [Fact]
    public void PrefixHelpers()
    {
        Assert.Equal(["a", "a/b"], PathRules.DirectoryPrefixes("a/b/c.txt").ToArray());
        Assert.Equal("a/b", PathRules.Parent("a/b/c.txt"));
        Assert.Null(PathRules.Parent("c.txt"));
        Assert.Equal("c.txt", PathRules.FileName("a/b/c.txt"));
    }
}
