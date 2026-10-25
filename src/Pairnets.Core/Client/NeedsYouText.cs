using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>The words on the "Needs you" page in both apps.</summary>
public static class NeedsYouText
{
    /// <summary>
    /// The line under the page title: how many things wait for a decision, and what the sync does meanwhile. It only says
    /// "keeps syncing" while it does: the Mac test (2026-10-24) saw it with every nest paused.
    /// </summary>
    public static string Subtitle(int count, StatusSnapshot status)
    {
        var meanwhile = status.Status == RunnerStatus.Paused || status.Paused ? "Syncing is paused."
            : status.ConnectionText == "Not connected" ? "Pairnets keeps trying to reach your nest."
            : null;
        return count switch
        {
            0 => "Nothing needs a decision right now. " + (meanwhile ?? "Everything keeps syncing."),
            1 => "1 thing only you can decide. " + (meanwhile ?? "Everything else keeps syncing meanwhile."),
            _ => $"{count} things only you can decide. " + (meanwhile ?? "Everything else keeps syncing meanwhile."),
        };
    }
}
