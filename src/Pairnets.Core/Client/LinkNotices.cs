namespace Pairnets.Core.Client;

/// <summary>One card on the Needs you page about a shared nest or a linked folder; both apps draw it.</summary>
/// <param name="NestId">For <see cref="AttentionKind.Shared"/>: the nest to add.</param>
/// <param name="Folder">For <see cref="AttentionKind.Linked"/>: the linked folder, a top-level folder of this nest.</param>
public sealed record LinkNotice(AttentionKind Kind, string Title, string Detail, string ActionLabel, string? NestId = null, string? Folder = null)
{
    /// <summary>The quieter second button of a shared-nest card.</summary>
    public const string NotNowLabel = "Not now";
}

/// <summary>
/// Needs you cards for nests that join nests (docs/DECISIONS.md, "4 nests per account, and linked nests"):
/// <list type="bullet">
/// <item>A nest shared with the owner's account that this computer doesn't sync yet: "Add it" runs the usual Add a nest
/// sign-in, where the shared nest is picked in the browser.</item>
/// <item>A linked folder that needs a look: the other nest has been off for a while, refused, or the link ended. A
/// linked folder that is in step needs nothing and gets no card.</item>
/// </list>
/// </summary>
public static class LinkNotices
{
    /// <summary>A link that last got through this long ago is worth a card; shorter gaps are a nest restarting.</summary>
    public static readonly TimeSpan WaitingCardAfter = TimeSpan.FromHours(6);

    /// <param name="have">The nests this computer already syncs.</param>
    /// <param name="dismissed">Shared nests the person answered "Not now" for (while the app runs).</param>
    public static IReadOnlyList<LinkNotice> Build(StatusSnapshot status, IEnumerable<string> have, IEnumerable<string> dismissed, DateTimeOffset now)
    {
        var skip = new HashSet<string>(have.Concat(dismissed), StringComparer.Ordinal);
        var list = new List<LinkNotice>();
        foreach (var o in status.SharedOffers.Where(o => !skip.Contains(o.NestId)))
        {
            var what = (o.Folders is { Count: > 0 } f ? string.Join(", ", f) : "The whole nest") + " · " + (o.Access == "edit" ? "can change" : "view only");
            list.Add(new LinkNotice(AttentionKind.Shared, $"{o.OwnerName} shared {o.NestName} with you",
                $"{what}. Add it to get it in its own folder on this computer.", "Add it", NestId: o.NestId));
        }
        foreach (var l in status.LinkedFolders)
        {
            var from = $"{l.OwnerName}'s {l.NestName}";
            var detail = l.State switch
            {
                "unlinked" => $"This folder is no longer linked with {from}. Your copy stays here and no longer changes.",
                "problem" => l.Message ?? $"{from} doesn't let this nest in any more. {l.OwnerName} can link it again.",
                "waiting" when l.LastInStep is not { } ok || now - ok > WaitingCardAfter =>
                    $"Waiting for {from}. Your copy works as normal; new changes come in when it's back.",
                _ => null,
            };
            if (detail is not null)
                list.Add(new LinkNotice(AttentionKind.Linked, l.Folder, detail, "Show folder", Folder: l.Folder));
        }
        return list;
    }
}
