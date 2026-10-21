using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>What the apps call each nest when this computer syncs several (the nest list, notices, the tray menu).</summary>
public static class NestNames
{
    /// <summary>
    /// The nest's name: the one its owner gave it (from the nest itself, <paramref name="status"/>, else the one saved at
    /// sign-in), else its address's host for a nest reached directly, else "Your nest" (or "Nest 2", "Nest 3" when there
    /// are several, so they can be told apart).
    /// </summary>
    public static string Title(ClientSettings nest, ClientSettings? all = null, StatusSnapshot? status = null)
    {
        if (status?.Server?.NestName is { } named && ComputerName.Clean(named) is { Length: > 0 } fromNest)
            return fromNest;
        if (ComputerName.Clean(nest.NestLabel) is { Length: > 0 } label)
            return label;
        if (!Relay.IsRelayAddress(nest.ServerUrl) && Uri.TryCreate(nest.ServerUrl, UriKind.Absolute, out var url))
            return url.Host;
        if (all is null || all.NestCount <= 1)
            return "Your nest";
        var index = all.Nests().ToList().FindIndex(n => n.NestId == nest.NestId);
        return index <= 0 ? "Your nest" : $"Nest {index + 1}";
    }

    /// <summary>"Work nest: " in front of a notice, when there is more than one nest (with one, notices read as before).</summary>
    public static string Prefix(ClientSettings nest, ClientSettings all, StatusSnapshot? status = null) =>
        all.NestCount > 1 ? Title(nest, all, status) + ": " : "";
}

/// <summary>
/// Signing in to one more nest from "Add a nest" (both apps' sign-in view): the nests this computer already syncs, so
/// the browser page greys them out, the folder gets its own default and the folder rules are checked, and the new
/// nest's key goes in a slot of its own.
/// </summary>
/// <param name="All">This computer's settings, with every nest it syncs.</param>
/// <param name="New">The settings the new nest starts from (<see cref="ClientSettings.BlankNest"/>).</param>
public sealed record NestAdding(ClientSettings All, ClientSettings New)
{
    /// <summary>Where the new nest's key is saved (Mac, Linux): see <see cref="ClientSettings.KeySlotFor"/>.</summary>
    public string? KeySlot => All.KeySlotFor(New);

    /// <summary>The nest ids of the account nests already synced here.</summary>
    public IEnumerable<string> Joined => All.Nests().Select(n => Relay.NestId(n.ServerUrl)).OfType<string>();

    /// <summary>The suggested folder for the new nest: Pairnets\&lt;its name&gt;, clear of the other nests' folders.</summary>
    public string SuggestedFolder(string nestName) => Settings.NestFolders.Suggested(nestName, All.Nests().Select(n => n.Folder));

    /// <summary>Why the new nest can't use <paramref name="folder"/>, or null.</summary>
    public string? FolderProblem(string folder) => All.FolderProblem(folder, New.NestId);

    /// <summary>This computer already syncs the nest at <paramref name="server"/>: its name, else null.</summary>
    public string? AlreadySynced(Uri server)
    {
        var address = Api.PairnetsApiClient.NormalizeBase(server).ToString();
        return All.Nests().FirstOrDefault(n => n.ServerUrl is { } u && string.Equals(Api.PairnetsApiClient.NormalizeBase(new Uri(u)).ToString(), address, StringComparison.OrdinalIgnoreCase)) is { } same
            ? NestNames.Title(same, All)
            : null;
    }
}
