using Tether.Core.Paths;

namespace Tether.Tests.Unit;

public class IgnoreListTests
{
    private readonly IgnoreList _defaults = new();

    [Theory]
    [InlineData("~$report.docx")]
    [InlineData("dir/~$report.docx")]
    [InlineData("x.tmp")]
    [InlineData("X.TMP")]
    [InlineData("a/b/c.temp")]
    [InlineData(".file.swp")]
    [InlineData(".file.swo")]
    [InlineData("notes.txt~")]
    [InlineData(".~lock.report.odt#")]
    [InlineData("Thumbs.db")]
    [InlineData("pics/thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("sub/Desktop.ini")]
    [InlineData(".DS_Store")]
    [InlineData("big.iso.crdownload")]
    [InlineData("movie.part")]
    [InlineData(".git/config")]
    [InlineData("project/.git/objects/ab/cdef")]
    [InlineData(".tether-tmp/abc.part")]
    [InlineData(".tether-marker")]
    public void DefaultsIgnore(string path) => Assert.True(_defaults.IsIgnored(path));

    [Theory]
    [InlineData("report.docx")]
    [InlineData("tmp/file.txt")]
    [InlineData("template.txt")]
    [InlineData("partial.txt")]
    [InlineData(".gitignore")]
    [InlineData("git/file")]
    [InlineData("a.git")]
    [InlineData("~report.docx")]
    public void DefaultsKeep(string path) => Assert.False(_defaults.IsIgnored(path));

    [Fact]
    public void DirectoryPatternsMatchFoldersOnly()
    {
        Assert.True(_defaults.IsIgnoredDirectory(".git"));
        Assert.True(_defaults.IsIgnoredDirectory("a/.git"));
        Assert.False(_defaults.IsIgnored(".git")); // a *file* named .git (worktree pointer) is synced
    }

    [Fact]
    public void UserPatterns()
    {
        var list = new IgnoreList(["*.bak", "node_modules/", "build/out", "# comment", "", "docs/**/*.pdf"]);
        Assert.True(list.IsIgnored("x.bak"));
        Assert.True(list.IsIgnored("web/node_modules/pkg/index.js"));
        Assert.True(list.IsIgnoredDirectory("node_modules"));
        Assert.True(list.IsIgnored("build/out"));
        Assert.True(list.IsIgnored("build/out/file.o"));
        Assert.False(list.IsIgnored("other/build/out/file.o"));
        Assert.True(list.IsIgnored("docs/a/b/c.pdf"));
        Assert.False(list.IsIgnored("docs/a.txt"));
        Assert.False(list.IsIgnored("# comment"));
    }

    [Fact]
    public void QuestionMarkMatchesOneCharacter()
    {
        var list = new IgnoreList(["file?.log"]);
        Assert.True(list.IsIgnored("file1.log"));
        Assert.False(list.IsIgnored("file12.log"));
    }
}
