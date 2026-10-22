namespace Pairnets.Core.Client;

/// <summary>What a keyboard shortcut in the main window does.</summary>
public enum ShortcutAction
{
    None,

    /// <summary>Puts the cursor in the page's search box (Activity, Files, Deleted).</summary>
    Search,
    SyncNow,

    /// <summary>Ctrl+, (⌘, on a Mac): the Settings page.</summary>
    Settings,

    // Ctrl+1 … Ctrl+6: the six page tiles, in the side panel's order.
    PageOverview,
    PageActivity,
    PageNeedsYou,
    PageFiles,
    PageDevices,
    PageSettings,

    /// <summary>Alt+Left (⌘[ on a Mac), the mouse's back button or a keyboard's Back key: the page before.</summary>
    Back,

    /// <summary>Alt+Right (⌘] on a Mac), the mouse's forward button or a keyboard's Forward key.</summary>
    Forward,
}

/// <summary>
/// The main window's keyboard shortcuts, the same in both apps: Ctrl+F search, Ctrl+R and F5 sync now, Ctrl+, Settings and
/// Ctrl+1…6 the pages, Alt+Left and Alt+Right back and forward. On a Mac the Command key stands in for Ctrl. Keys come in by their name, which WPF's and Avalonia's
/// Key enums share ("F", "R", "F5", "OemComma", "D1", "NumPad1").
/// </summary>
public static class Shortcuts
{
    /// <summary>The action for <paramref name="key"/>; <paramref name="command"/> is Ctrl (⌘ on a Mac) held, with no other modifier.</summary>
    public static ShortcutAction For(string key, bool command)
    {
        if (key == "F5")
            return command ? ShortcutAction.None : ShortcutAction.SyncNow;
        if (!command)
            return ShortcutAction.None;
        return key switch
        {
            "F" => ShortcutAction.Search,
            "R" => ShortcutAction.SyncNow,
            "OemComma" => ShortcutAction.Settings,
            "D1" or "NumPad1" => ShortcutAction.PageOverview,
            "D2" or "NumPad2" => ShortcutAction.PageActivity,
            "D3" or "NumPad3" => ShortcutAction.PageNeedsYou,
            "D4" or "NumPad4" => ShortcutAction.PageFiles,
            "D5" or "NumPad5" => ShortcutAction.PageDevices,
            "D6" or "NumPad6" => ShortcutAction.PageSettings,
            _ => ShortcutAction.None,
        };
    }

    /// <summary>"Ctrl+R" (Windows, Linux) or "⌘R" (Mac); empty for an action without a key.</summary>
    public static string Hint(ShortcutAction action, bool mac)
    {
        var key = action switch
        {
            ShortcutAction.Search => "F",
            ShortcutAction.SyncNow => "R",
            ShortcutAction.Settings => ",",
            ShortcutAction.PageOverview => "1",
            ShortcutAction.PageActivity => "2",
            ShortcutAction.PageNeedsYou => "3",
            ShortcutAction.PageFiles => "4",
            ShortcutAction.PageDevices => "5",
            ShortcutAction.PageSettings => "6",
            _ => null,
        };
        return action switch
        {
            ShortcutAction.Back => mac ? "⌘[" : "Alt+Left",
            ShortcutAction.Forward => mac ? "⌘]" : "Alt+Right",
            _ => key is null ? string.Empty : mac ? "⌘" + key : "Ctrl+" + key,
        };
    }

    /// <summary>
    /// Back or forward for <paramref name="key"/>, or None. <paramref name="alt"/> and <paramref name="command"/> are Alt and
    /// Ctrl (⌘ on a Mac) held, each alone. On a Mac it is ⌘[ and ⌘] (Alt+arrows move by word there); elsewhere Alt+Left
    /// and Alt+Right. A keyboard's own Back and Forward keys work everywhere.
    /// </summary>
    public static ShortcutAction ForHistory(string key, bool alt, bool command, bool mac)
    {
        if (!alt && !command)
            return key switch
            {
                "BrowserBack" => ShortcutAction.Back,
                "BrowserForward" => ShortcutAction.Forward,
                _ => ShortcutAction.None,
            };
        return (mac, alt, command, key) switch
        {
            (true, false, true, "OemOpenBrackets") => ShortcutAction.Back,
            (true, false, true, "OemCloseBrackets") => ShortcutAction.Forward,
            (false, true, false, "Left") => ShortcutAction.Back,
            (false, true, false, "Right") => ShortcutAction.Forward,
            _ => ShortcutAction.None,
        };
    }

    /// <summary>"Sync now (Ctrl+R)": a tooltip with its shortcut.</summary>
    public static string WithHint(string tip, ShortcutAction action, bool mac) =>
        Hint(action, mac) is { Length: > 0 } hint ? $"{tip} ({hint})" : tip;
}
