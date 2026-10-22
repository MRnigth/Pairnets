using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>The main window's keyboard shortcuts (A4), shared by both apps.</summary>
public class UiShortcutsTests
{
    [Theory]
    [InlineData("F", true, ShortcutAction.Search)]
    [InlineData("R", true, ShortcutAction.SyncNow)]
    [InlineData("F5", false, ShortcutAction.SyncNow)]
    [InlineData("OemComma", true, ShortcutAction.Settings)]
    [InlineData("D1", true, ShortcutAction.PageOverview)]
    [InlineData("D2", true, ShortcutAction.PageActivity)]
    [InlineData("D3", true, ShortcutAction.PageNeedsYou)]
    [InlineData("D4", true, ShortcutAction.PageFiles)]
    [InlineData("NumPad5", true, ShortcutAction.PageDevices)]
    [InlineData("D6", true, ShortcutAction.PageSettings)]
    public void KeysMapToActions(string key, bool command, ShortcutAction action) => Assert.Equal(action, Shortcuts.For(key, command));

    [Theory]
    [InlineData("R", false)]
    [InlineData("F", false)]
    [InlineData("D1", false)]
    [InlineData("F5", true)]
    [InlineData("D7", true)]
    [InlineData("Q", true)]
    public void OtherKeysDoNothing(string key, bool command) => Assert.Equal(ShortcutAction.None, Shortcuts.For(key, command));

    [Fact]
    public void HintsUseCtrlOrTheCommandKey()
    {
        Assert.Equal("Ctrl+R", Shortcuts.Hint(ShortcutAction.SyncNow, mac: false));
        Assert.Equal("⌘,", Shortcuts.Hint(ShortcutAction.Settings, mac: true));
        Assert.Equal("Overview (Ctrl+1)", Shortcuts.WithHint("Overview", ShortcutAction.PageOverview, mac: false));
        Assert.Equal("Computers (⌘5)", Shortcuts.WithHint("Computers", ShortcutAction.PageDevices, mac: true));
        Assert.Equal("More", Shortcuts.WithHint("More", ShortcutAction.None, mac: false));
    }

    [Theory]
    [InlineData("Left", true, false, false, ShortcutAction.Back)]
    [InlineData("Right", true, false, false, ShortcutAction.Forward)]
    [InlineData("OemOpenBrackets", false, true, true, ShortcutAction.Back)]
    [InlineData("OemCloseBrackets", false, true, true, ShortcutAction.Forward)]
    [InlineData("BrowserBack", false, false, false, ShortcutAction.Back)]
    [InlineData("BrowserForward", false, false, true, ShortcutAction.Forward)]
    [InlineData("Left", false, false, false, ShortcutAction.None)]
    [InlineData("Left", true, false, true, ShortcutAction.None)] // Alt+Left moves by word on a Mac
    [InlineData("Left", true, true, false, ShortcutAction.None)]
    [InlineData("OemOpenBrackets", false, true, false, ShortcutAction.None)]
    [InlineData("BrowserBack", true, false, false, ShortcutAction.None)]
    public void BackAndForwardKeys(string key, bool alt, bool command, bool mac, ShortcutAction action) =>
        Assert.Equal(action, Shortcuts.ForHistory(key, alt, command, mac));

    [Fact]
    public void BackAndForwardHints()
    {
        Assert.Equal("Back (Alt+Left)", Shortcuts.WithHint("Back", ShortcutAction.Back, mac: false));
        Assert.Equal("Forward (⌘])", Shortcuts.WithHint("Forward", ShortcutAction.Forward, mac: true));
    }
}
