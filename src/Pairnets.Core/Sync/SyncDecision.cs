namespace Pairnets.Core.Sync;

/// <summary>What the engine must do for one path.</summary>
public enum SyncAction
{
    /// <summary>Local, server and base already agree.</summary>
    None,

    /// <summary>Local and server have identical content; remember it as the new base.</summary>
    RecordBase,

    /// <summary>The file is gone on both sides; forget the stale base.</summary>
    ClearBase,

    /// <summary>Only the server changed (or the file is new there): download it.</summary>
    Download,

    /// <summary>Only this machine changed (or the file is new here): upload it with base = server hash or "none".</summary>
    Upload,

    /// <summary>Both sides changed differently: keep both (local becomes a conflict copy).</summary>
    Conflict,

    /// <summary>Deleted on the server and untouched here: delete the local copy.</summary>
    DeleteLocal,

    /// <summary>Deleted here and untouched on the server: delete it on the server.</summary>
    DeleteRemote,
}

/// <summary>
/// The three-hash decision. L = local content hash now, S = server hash now, B = the hash
/// both sides agreed on at the last successful sync. Null means "absent" (or tombstoned).
/// This function is pure: no I/O, no clock, no state.
/// </summary>
public static class SyncDecision
{
    public static SyncAction Decide(string? local, string? server, string? @base)
    {
        // Rule 1: both sides agree (including both absent).
        if (Same(local, server))
        {
            if (local is null)
                return @base is null ? SyncAction.None : SyncAction.ClearBase;
            return Same(@base, local) ? SyncAction.None : SyncAction.RecordBase;
        }

        // Rule 2: both exist and differ.
        if (local is not null && server is not null)
        {
            if (Same(@base, local))
                return SyncAction.Download;
            if (Same(@base, server))
                return SyncAction.Upload;
            return SyncAction.Conflict;
        }

        // Rule 3: only local exists. Edits beat deletions.
        if (local is not null)
            return Same(@base, local) ? SyncAction.DeleteLocal : SyncAction.Upload;

        // Rule 4: only the server has it.
        return Same(@base, server) ? SyncAction.DeleteRemote : SyncAction.Download;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);
}
