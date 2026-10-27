using Pairnets.Core.Paths;

namespace Pairnets.Core.Client;

/// <summary>
/// "Ignore in Pairnets…" on a folder's right-click menu: one ignore rule for exactly that folder ("/Work/game/Library/"),
/// added to or taken from this computer's rules. Rules stay on this computer; nothing is deleted on the nest or the other
/// computers, the folder just stops syncing here.
/// </summary>
public static class IgnoreFolder
{
    /// <summary>The rule for the folder at <paramref name="syncPath"/>: "/Work/game/Library/" (only that folder).</summary>
    public static string Rule(string syncPath) => "/" + syncPath.Replace('\\', '/').Trim('/') + "/";

    /// <summary>True when <paramref name="rules"/> hold this folder's own rule (as written by <see cref="Rule"/>, or by hand).</summary>
    public static bool IsOwnRule(IEnumerable<string> rules, string syncPath) => rules.Any(r => SameRule(r, syncPath));

    /// <summary><paramref name="rules"/> with this folder's rule added (once).</summary>
    public static List<string> With(IEnumerable<string> rules, string syncPath)
    {
        var list = rules.ToList();
        if (!IsOwnRule(list, syncPath))
            list.Add(Rule(syncPath));
        return list;
    }

    /// <summary><paramref name="rules"/> without this folder's own rule; other rules stay.</summary>
    public static List<string> Without(IEnumerable<string> rules, string syncPath) => rules.Where(r => !SameRule(r, syncPath)).ToList();

    /// <summary>
    /// What the right-click item does for <paramref name="syncPath"/>: a question whose Yes saves <see cref="Choice.Rules"/>,
    /// or (when <see cref="Choice.Rules"/> is null) just a message.
    /// </summary>
    public static Choice For(IReadOnlyList<string> rules, string syncPath, bool isFolder)
    {
        if (syncPath.Length == 0 || !isFolder)
            return new Choice(OnlyFoldersText, null);
        if (IsOwnRule(rules, syncPath))
            return new Choice(AskSyncAgainText(syncPath), Without(rules, syncPath), "Sync it again");
        if (new IgnoreList(rules).IsIgnoredDirectory(syncPath))
            return new Choice(OtherRuleText(syncPath), null);
        return new Choice(AskText(syncPath), With(rules, syncPath), "Ignore");
    }

    /// <summary>The text to show, the rules to save on Yes (null: only a message), and the Yes button's label.</summary>
    public sealed record Choice(string Text, List<string>? Rules, string Yes = "OK");

    /// <summary>The question before ignoring.</summary>
    public static string AskText(string syncPath) =>
        $"Stop syncing \"{PathRules.FileName(syncPath)}\"?\n\nIt stays as it is on this computer and on your nest. Changes inside it "
        + "won't sync from this computer any more. Your other computers keep syncing it unless you ignore it there too.";

    /// <summary>The question when the folder is ignored by its own rule.</summary>
    public static string AskSyncAgainText(string syncPath) =>
        $"\"{PathRules.FileName(syncPath)}\" is ignored on this computer.\n\nSync it again?";

    /// <summary>When another rule (a name, a pattern) skips the folder: it can only be changed in Settings.</summary>
    public static string OtherRuleText(string syncPath) =>
        $"Pairnets already skips \"{PathRules.FileName(syncPath)}\" because of an ignore rule.\n\n"
        + "You can change the rules in Settings → Advanced: files to ignore.";

    /// <summary>For a file or the whole folder.</summary>
    public const string OnlyFoldersText = "Only folders inside your Pairnets folder can be ignored.";

    private static bool SameRule(string rule, string syncPath)
    {
        var a = rule.Trim().Replace('\\', '/').Trim('/');
        var b = syncPath.Replace('\\', '/').Trim('/');
        // "Library/" without a slash before it matches every Library folder: only the same rule when the folder is at the top.
        if (!rule.Trim().Replace('\\', '/').StartsWith('/') && !a.Contains('/') && b.Contains('/'))
            return false;
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
