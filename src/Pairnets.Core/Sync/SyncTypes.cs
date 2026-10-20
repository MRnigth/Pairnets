using Pairnets.Core.State;

namespace Pairnets.Core.Sync;

/// <summary>Why a pass refused to run. Nothing is touched when a pass is blocked.</summary>
public enum BlockReason
{
    None,

    /// <summary>The configured sync folder does not exist (unplugged drive, moved folder).</summary>
    FolderMissing,

    /// <summary>State exists but the folder has no .pairnets-marker (wrong folder, drive glitch).</summary>
    MarkerMissing,

    /// <summary>The folder's marker belongs to a different sync state.</summary>
    MarkerMismatch,

    /// <summary>No state yet, but the folder already has a marker from another installation. Ask before adopting.</summary>
    ForeignMarker,

    /// <summary>The folder is empty while state still tracks files.</summary>
    FolderEmpty,

    /// <summary>The pass would delete too many files on one side.</summary>
    MassDelete,

    /// <summary>The server has a different identity than the one this folder was synced with.</summary>
    ServerChanged,

    /// <summary>The server's version went backwards (restored from an older backup).</summary>
    ServerRolledBack,

    /// <summary>This computer was removed from the nest, or the shared token it uses was turned off: sign in again.</summary>
    SignedOut,

    /// <summary>Still set up with the shared token although the nest signs computers in: sign in to keep syncing.</summary>
    SignInRequired,

    /// <summary>Signed out on purpose from this computer's own Sign out button: nothing is wrong, nothing syncs.</summary>
    SignedOutByYou,
}

/// <summary>What a running pass is doing now, so the apps can say it instead of a bare "Syncing".</summary>
public enum SyncStage
{
    None,

    /// <summary>Reading the server's list of files.</summary>
    ReadingServer,

    /// <summary>Checking the files in the folder (most from the saved fingerprints, new or changed ones are read).</summary>
    Checking,

    /// <summary>Uploading, downloading and deleting.</summary>
    Transferring,
}

public enum PassOutcome
{
    Completed,
    Blocked,
    Offline,
    AuthFailed,

    /// <summary>A local problem (folder unreadable, state DB error) stopped the pass.</summary>
    Failed,
}

/// <summary>Options for one pass.</summary>
/// <param name="DeferDownloads">
/// Skip downloads this pass (they count as deferred and happen on a later pass). Used while another
/// computer is still uploading a large batch, so this one fetches it in one go afterwards.
/// </param>
public sealed record PassOptions(string Reason = "manual", bool FullManifest = false, bool DeferDownloads = false);

public sealed record ConflictInfo(string Path, string ConflictCopyPath);

public sealed record PathWarningInfo(string Path, string Code, string? Message);

public sealed record SyncProgress(string? CurrentPath, string? Operation, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>
/// How far the look through the folder is: files looked at, how many of them had to be read (new or changed), and the
/// file being read right now with its bytes read so far (big files take a while), and the files in the folder (0 while
/// it is still being listed).
/// </summary>
public sealed record ScanProgress(int FilesChecked, int FilesRead, string? ReadingPath, long ReadingBytes, long ReadingSize, int FilesTotal = 0);

/// <summary>Summary of one pass.</summary>
public sealed class PassResult
{
    public PassOutcome Outcome { get; set; } = PassOutcome.Completed;
    public BlockReason BlockReason { get; set; }
    public string? Message { get; set; }
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int DeletedLocal { get; set; }
    public int DeletedRemote { get; set; }
    public int Conflicts { get; set; }
    public int Recorded { get; set; }
    public int Errors { get; set; }

    /// <summary>Copies removed from this computer because their folder lives on the nest only (they are safe there).</summary>
    public int Freed { get; set; }

    /// <summary>Copies of nest-only files kept here this pass because the nest is (nearly) full.</summary>
    public int KeptForRoom { get; set; }

    /// <summary>Files skipped because they were still being written, locked, or otherwise unknown.</summary>
    public int Unstable { get; set; }

    /// <summary>Uploads/deletes that got 409 because the server moved on; retried next pass.</summary>
    public int Deferred { get; set; }

    /// <summary>Downloads skipped because another computer is still uploading a big batch.</summary>
    public int HeldDownloads { get; set; }

    /// <summary>Paths that need the user's attention (invalid name, case collision).</summary>
    public int Warnings { get; set; }

    /// <summary>The server version this pass worked from (0 when it stopped before reading the server's list).</summary>
    public long ServerVersion { get; set; }

    /// <summary>
    /// True when this pass left nothing behind: this computer then has every file up to <see cref="ServerVersion"/>
    /// (errors, waiting downloads or changes the server turned back mean it doesn't yet).
    /// </summary>
    public bool CaughtUp => Outcome == PassOutcome.Completed && ServerVersion > 0 && Errors == 0 && Deferred == 0 && HeldDownloads == 0;

    public List<(DeleteSide Side, string Path)> BlockedDeletes { get; } = [];
    public List<ConflictInfo> ConflictCopies { get; } = [];
    public List<string> ErrorMessages { get; } = [];

    /// <summary>Number of transfers and deletions that changed something.</summary>
    public int Changes => Uploaded + Downloaded + DeletedLocal + DeletedRemote + Conflicts;

    /// <summary>True when another pass soon is likely to make progress (unstable files, deferred, errors).</summary>
    public bool WantsRetry => Outcome == PassOutcome.Completed && (Unstable > 0 || Deferred > 0 || Errors > 0);

    public override string ToString() => Outcome switch
    {
        PassOutcome.Blocked => $"blocked ({BlockReason}): {Message}",
        PassOutcome.Offline => $"offline: {Message}",
        PassOutcome.AuthFailed => "authentication failed",
        PassOutcome.Failed => $"failed: {Message}",
        _ => $"up {Uploaded}, down {Downloaded}, del-local {DeletedLocal}, del-remote {DeletedRemote}, conflicts {Conflicts}, recorded {Recorded}, freed {Freed}, errors {Errors}, unstable {Unstable}, deferred {Deferred}",
    };
}

/// <summary>Engine configuration.</summary>
public sealed class EngineOptions
{
    public required string Folder { get; init; }
    public required string DeviceName { get; init; }
    public IReadOnlyList<string> ExtraIgnore { get; init; } = [];
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RacyWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Block when a pass would delete more than this fraction of tracked files on one side...</summary>
    public double MassDeleteFraction { get; init; } = 0.20;

    /// <summary>...or more than this many files, whichever limit is smaller.</summary>
    public int MassDeleteMax { get; init; } = 50;

    /// <summary>
    /// Uploads (and downloads) run this many at a time. Each small file costs a network round trip,
    /// so a few in flight hide the latency; 1 restores strictly one-by-one transfers.
    /// </summary>
    public int MaxParallelTransfers { get; init; } = 4;

    /// <summary>
    /// Downloads of files at least this big keep what arrived when the connection breaks (or syncing is paused or
    /// the app closes) and later ask only for the rest. Smaller files take about a minute or less even on a slow
    /// link, so they simply start again, as before.
    /// </summary>
    public long ResumeDownloadsFrom { get; init; } = 16L * 1024 * 1024;

    /// <summary>The folder as a Windows cloud folder (ticks in Explorer, Files On-Demand), or a plain folder.</summary>
    public ICloudFiles CloudFiles { get; init; } = ICloudFiles.None;

    /// <summary>Where each folder's files live on this computer ("Where your files live"); by default all here.</summary>
    public PlacementMap Placement { get; init; } = PlacementMap.Everywhere;
}

/// <summary>Test seams. Production code never sets these.</summary>
public sealed class EngineHooks
{
    /// <summary>Wraps the stream a download is written to (e.g. to simulate a full disk).</summary>
    public Func<Stream, Stream>? WrapDownloadStream { get; set; }

    /// <summary>Called after each executed action, with the path. May throw or block. Transfers may call it concurrently.</summary>
    public Func<SyncAction, string, Task>? AfterAction { get; set; }

    /// <summary>Called when an upload or download starts, before any network traffic. May block.</summary>
    public Func<SyncAction, string, Task>? BeforeTransfer { get; set; }
}
