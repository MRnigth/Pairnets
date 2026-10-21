using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Tests.Unit;

/// <summary>The Needs you cards for shared nests and linked folders (LinkNotices), which both apps draw.</summary>
public sealed class LinkNoticesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static StatusSnapshot Status(IReadOnlyList<SharedNestOffer>? offers = null, IReadOnlyList<LinkedFolder>? links = null) =>
        StatusSnapshot.WithoutSession(new ClientSettings()) with { SharedOffers = offers ?? [], LinkedFolders = links ?? [] };

    [Fact]
    public void ASharedNestIsOfferedUnlessThisComputerHasItOrSaidNotNow()
    {
        var offers = new[]
        {
            new SharedNestOffer("nst_a", "Home nest", "Oskar", "view", ["Photos", "School"]),
            new SharedNestOffer("nst_b", "Band nest", "Jonas", "edit"),
            new SharedNestOffer("nst_c", "Cabin", "Oskar", "view"),
        };
        var cards = LinkNotices.Build(Status(offers), have: ["nst_b"], dismissed: ["nst_c"], Now);
        var card = Assert.Single(cards);
        Assert.Equal(AttentionKind.Shared, card.Kind);
        Assert.Equal("Oskar shared Home nest with you", card.Title);
        Assert.Equal("Photos, School · view only. Add it to get it in its own folder on this computer.", card.Detail);
        Assert.Equal("Add it", card.ActionLabel);
        Assert.Equal("nst_a", card.NestId);
    }

    [Fact]
    public void ALinkedFolderOnlyGetsACardWhenItNeedsALook()
    {
        var links = new[]
        {
            new LinkedFolder("From Oskar – Home nest", "Oskar", "Home nest", "view", "in_step", Now),
            new LinkedFolder("From Jonas – Band", "Jonas", "Band", "view", "waiting", Now.AddMinutes(-20)),
            new LinkedFolder("From Oskar – Cabin", "Oskar", "Cabin", "view", "waiting", Now.AddDays(-1)),
            new LinkedFolder("From Ida – Work", "Ida", "Work", "edit", "unlinked"),
            new LinkedFolder("From Bo – Music", "Bo", "Music", "view", "problem", null, "The other nest no longer lets this nest in."),
        };
        var cards = LinkNotices.Build(Status(links: links), [], [], Now);
        Assert.Equal(["From Oskar – Cabin", "From Ida – Work", "From Bo – Music"], cards.Select(c => c.Title));
        Assert.All(cards, c => Assert.Equal(AttentionKind.Linked, c.Kind));
        Assert.StartsWith("Waiting for Oskar's Cabin.", cards[0].Detail);
        Assert.Contains("no longer linked", cards[1].Detail);
        Assert.Equal("The other nest no longer lets this nest in.", cards[2].Detail);
        Assert.Equal("From Oskar – Cabin", cards[0].Folder);
    }
}
