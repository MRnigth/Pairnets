using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>
/// The "Let your nest update by itself?" card at the top of the Overview page, shown after this app updated itself while
/// a nest of the person's own still waits to be asked before it updates (<see cref="ClientSettings.AutoUpdateServer"/>
/// off), so security fixes reach the nest as soon as this app has them. A friend's nest is never named: only its owner
/// can update it. Turn on and Not now both put the card away until the next app update.
/// </summary>
public sealed record NestAutoUpdateOffer(IReadOnlyList<NestAutoUpdateOffer.Nest> Nests)
{
    /// <summary>One nest the offer names: its id (<see cref="ClientSettings.NestId"/>) and the name the apps show for it.</summary>
    public sealed record Nest(string Id, string Name);

    /// <summary>
    /// This start is the first one of a newer (or other) version than the one that ran here before. A computer that set
    /// up Pairnets before the version was remembered counts as updated; a fresh install, not yet set up, does not.
    /// </summary>
    public static bool JustUpdated(ClientSettings settings, string current) =>
        settings.LastRunVersion is { Length: > 0 } last
            ? !string.Equals(last, current, StringComparison.OrdinalIgnoreCase)
            : settings.FirstRunCompleted;

    /// <summary>
    /// Remembers this start's version, and after an update that the card is due (<see cref="ClientSettings.NestAutoUpdateOfferFor"/>).
    /// True when <paramref name="settings"/> changed and needs saving.
    /// </summary>
    public static bool NoteStart(ClientSettings settings, string current)
    {
        var updated = JustUpdated(settings, current);
        if (!updated && string.Equals(settings.LastRunVersion, current, StringComparison.OrdinalIgnoreCase))
            return false;
        settings.LastRunVersion = current;
        if (updated)
            settings.NestAutoUpdateOfferFor = current;
        return true;
    }

    /// <summary>
    /// The card for <paramref name="nests"/> (<see cref="ClientSettings.Nests"/>), or null when no update brought it, it
    /// was answered, or every nest updates by itself or is a friend's.
    /// </summary>
    /// <param name="all">This computer's settings (the app-wide answer).</param>
    /// <param name="skip">Nests to leave out: the "Update your nest" window already asked about them this time.</param>
    public static NestAutoUpdateOffer? For(ClientSettings all, IEnumerable<ClientSettings> nests, string current, Func<string, bool>? skip = null)
    {
        if (!string.Equals(all.NestAutoUpdateOfferFor, current, StringComparison.OrdinalIgnoreCase))
            return null;
        var named = nests
            .Where(n => n.NestId is not null && n.IsComplete && !n.AutoUpdateServer && n.ShareAccess().IsOwner && skip?.Invoke(n.NestId) != true)
            .Select(n => new Nest(n.NestId!, NestNames.Title(n, all)))
            .ToList();
        return named.Count == 0 ? null : new NestAutoUpdateOffer(named);
    }

    public string Title => Nests.Count == 1 ? "Let your nest update by itself?" : "Let your nests update by themselves?";

    public string Detail => Nests.Count == 1
        ? $"Pairnets just updated on this computer. {Nests[0].Name} only updates when you say so, so security fixes can reach it late."
        : $"Pairnets just updated on this computer. {Nests.Count} of your nests only update when you say so, so security fixes can reach them late.";

    public const string TurnOnLabel = "Turn on";

    public const string NotNowLabel = "Not now";

    /// <summary>The notice after Turn on.</summary>
    public string TurnedOn => Nests.Count == 1
        ? $"{Nests[0].Name} now updates by itself"
        : $"{Nests.Count} nests now update by themselves";

    public const string TurnedOnDetail = "You can change this any time in Settings.";
}

/// <summary>The two buttons of <see cref="NestAutoUpdateOffer"/>.</summary>
public enum NestAutoUpdateAnswer
{
    TurnOn,
    NotNow,
}
