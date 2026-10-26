using System.Text.Json;
using Pairnets.Core.State;

namespace Pairnets.Core.Sync;

/// <summary>One file of a big delete: its name and the content it had, so "Put them all back" finds that version on the nest.</summary>
public sealed record BigDeleteFile(string Path, string? Hash);

/// <summary>
/// The last big delete (100 or more files in one pass) this computer carried out, asked or not. Kept so the app can
/// offer "Put them all back": the nest still has every one of them in its history (30 days), and this computer's
/// copies went to the Recycle Bin (Trash). <see cref="FromHere"/>: deleted on this computer (false: another
/// computer deleted them and this one followed).
/// </summary>
public sealed record BigDeleteBatch(DateTimeOffset At, bool FromHere, IReadOnlyList<BigDeleteFile> Files)
{
    private const string Key = "bigDelete.last";

    /// <summary>Set to true once "Put them all back" ran (or the person closed the notice), so it is not offered again.</summary>
    public bool Done { get; init; }

    public int Count => Files.Count;

    public static BigDeleteBatch? Load(StateDb state)
    {
        try
        {
            return state.GetMeta(Key) is { Length: > 0 } json ? JsonSerializer.Deserialize<BigDeleteBatch>(json, PairnetsJson.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(StateDb state, BigDeleteBatch? batch) =>
        state.SetMeta(Key, batch is null ? string.Empty : JsonSerializer.Serialize(batch, PairnetsJson.Options));
}
