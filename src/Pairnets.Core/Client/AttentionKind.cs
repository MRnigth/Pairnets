namespace Pairnets.Core.Client;

/// <summary>What a card on the Needs you page is about (it picks the card's icon and colour).</summary>
public enum AttentionKind
{
    /// <summary>Something that needs a decision or a fix: a blocked sync, a file name that cannot be used.</summary>
    Warning,

    /// <summary>Another computer asks to join the nest (it has a code).</summary>
    Join,

    /// <summary>Both computers changed a file; both copies were kept.</summary>
    Conflict,

    /// <summary>The same name in different capital letters.</summary>
    NameClash,
}
