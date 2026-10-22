using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>
/// When the nest keeps a computer's old name, both apps point to where renaming is: Settings, not the Account page
/// (QA sweep 2026-10-22).
/// </summary>
public class RenameFailedTextTests
{
    [Fact]
    public void TheMessage_NamesTheComputerAndPointsToSettings()
    {
        var text = SignInTexts.RenameFailed("PC-1");
        Assert.Contains("so it stays PC-1", text);
        Assert.Contains("Settings page", text);
        Assert.Contains("\"This computer's name\"", text);
        Assert.DoesNotContain("Account", text);
    }
}
