using System.Text.Json;
using Pairnets.Core.State;

namespace Pairnets.Core.Client;

/// <summary>
/// The files deleted for good from this computer ("Help me clean up"). The nest keeps an ordinary "deleted" entry for
/// them, so other computers delete their copies, but has nothing left to restore: the Deleted list leaves them out
/// instead of offering a Restore that can only fail. Kept per deletion (the entry's version), so the same name deleted
/// again later is listed again.
/// </summary>
public static class PurgedFiles
{
    private const string Key = "files.purged";

    /// <summary>The most remembered; the oldest go first (the nest forgets deleted files after 30 days anyway).</summary>
    public const int Max = 2000;

    public static void Add(StateDb state, ManifestEntry entry)
    {
        var all = Load(state).ToList();
        var id = Id(entry);
        if (all.Contains(id))
            return;
        all.Add(id);
        if (all.Count > Max)
            all.RemoveRange(0, all.Count - Max);
        state.SetMeta(Key, JsonSerializer.Serialize(all));
    }

    public static IReadOnlyCollection<string> Load(StateDb state)
    {
        try
        {
            return state.GetMeta(Key) is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Whether this entry of the nest's list is one of the deletions made for good here.</summary>
    public static bool Contains(IReadOnlyCollection<string> purged, ManifestEntry entry) =>
        entry.Deleted && purged.Contains(Id(entry));

    private static string Id(ManifestEntry entry) => entry.Version + "|" + entry.Path;
}
