using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>
/// "Save a copy…" offers the file's own type, so a copy saved under the default name (or a new one) keeps its extension.
/// The QA sweep of 2026-10-22 found the copy of qa-seed.txt saved as "qa-seed".
/// </summary>
public class SaveCopyTypeTests
{
    [Theory]
    [InlineData("qa-seed.txt", "txt", "TXT file")]
    [InlineData("P1040504.JPG", "JPG", "JPG file")]
    [InlineData("archive.tar.gz", "gz", "GZ file")]
    public void AFileWithAnExtension_IsOfferedAsItsOwnType(string name, string extension, string label)
    {
        Assert.Equal((extension, label), FilesPageText.SaveCopyType(name));
    }

    [Theory]
    [InlineData("Makefile")]
    [InlineData("ends-with-dot.")]
    [InlineData("")]
    public void ANameWithoutAnExtension_GetsNoType(string name)
    {
        Assert.Null(FilesPageText.SaveCopyType(name));
    }
}
