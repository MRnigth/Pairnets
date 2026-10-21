using System.Globalization;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>
/// The words both apps use for nests shared with friends (friend invites, 1.0.61.2): "Shared by Oskar", "View only",
/// "Whole nest", the held-back card. Plain words only: "friend", "People", never "member" or "role".
/// </summary>
public static class ShareText
{
    public const string ViewOnly = "View only";
    public const string CanChange = "Can change";
    public const string WholeNest = "Whole nest";
    public const string KeepMine = "Keep mine here";

    /// <summary>"View only" or "Can change" for an access level ("view", "edit").</summary>
    public static string Pill(string? level) => level == NestAccess.View ? ViewOnly : CanChange;

    /// <summary>"view only" or "can change", inside a line.</summary>
    public static string Words(string? level) => level == NestAccess.View ? "view only" : "can change";

    /// <summary>"Photos, School", or "Whole nest" when the whole nest is shared.</summary>
    public static string Folders(IReadOnlyList<string>? folders) =>
        folders is null ? WholeNest : folders.Count == 0 ? "Nothing yet" : string.Join(", ", folders);

    /// <summary>The owner's name as a friend sees it ("Oskar"), or a stand-in while it is not known.</summary>
    public static string Owner(NestAccess access) => access.SharedBy ?? "the owner";

    /// <summary>"Shared by Oskar".</summary>
    public static string SharedBy(NestAccess access) => "Shared by " + Owner(access);

    /// <summary>"Shared by Oskar · view only": the line under a shared nest's name.</summary>
    public static string SharedLine(NestAccess access) => SharedBy(access) + " · " + Words(access.Level);

    /// <summary>"Oskar stopped sharing Home nest": a friend's computer removed from a shared nest.</summary>
    public static string StoppedSharing(NestAccess access) =>
        $"{(access.SharedBy is { } who ? who : "Its owner")} stopped sharing {access.NestName ?? "this nest"}. Your files here stay as they are.";

    /// <summary>"3 changes stay on this computer".</summary>
    public static string HeldBackTitle(int count) =>
        count == 1 ? "1 change stays on this computer" : $"{count.ToString(CultureInfo.InvariantCulture)} changes stay on this computer";

    /// <summary>
    /// "You can only view Home nest, so your edits to trip.jpg, notes.docx and 1 more aren't sent to Oskar. Put back
    /// Oskar's version, or keep yours (it stays only here and won't sync)."
    /// </summary>
    public static string HeldBackDetail(NestAccess access, string nest, IReadOnlyList<string> paths)
    {
        var names = paths.Select(p => Paths.PathRules.FileName(p)).Take(2).ToList();
        var more = paths.Count - names.Count;
        var list = names.Count switch
        {
            0 => "files",
            1 when more == 0 => names[0],
            _ when more == 0 => names[0] + " and " + names[1],
            _ => string.Join(", ", names) + $" and {more.ToString(CultureInfo.InvariantCulture)} more",
        };
        var owner = Owner(access);
        return $"You can only view {nest}, so your edits to {list} aren't sent to {owner}. Put back {Possessive(owner)} version, or keep yours (it stays only here and won't sync).";
    }

    /// <summary>"Put back Oskar's version".</summary>
    public static string PutBack(NestAccess access) => $"Put back {Possessive(Owner(access))} version";

    /// <summary>"Oskar's", "the owner's".</summary>
    public static string Possessive(string name) => name + "'s";
}
