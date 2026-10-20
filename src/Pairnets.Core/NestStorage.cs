namespace Pairnets.Core;

/// <summary>
/// One drive of the Linux nest, as its root helper lists it (deploy/place.sh, drives.json). <see cref="Folder"/> is what a
/// move to this drive asks for; a non-empty <see cref="Problem"/> says in plain words why the drive can't hold the nest.
/// <see cref="Free"/> and <see cref="Total"/> are in bytes.
/// </summary>
public sealed record StorageDrive(
    string Mount,
    string? Label,
    string? Fs,
    long Free,
    long Total,
    string Folder,
    string? Problem = null,
    bool Main = false)
{
    /// <summary>True when the nest's files can go on this drive.</summary>
    public bool Usable => string.IsNullOrWhiteSpace(Problem);
}

/// <summary>
/// drives.json in the nest's update folder, written by the root helper (deploy/place.sh) when the nest asks for the list:
/// where the files are now (<see cref="Current"/>), the usual folder on the main disk, whether the place was set up by
/// hand (in /etc/fstab: the apps can't change it then), when it was listed, and the drives.
/// </summary>
public sealed record StorageDrivesFile(
    string? Current,
    string? MainDisk,
    bool ByHand,
    DateTimeOffset? ListedAt,
    IReadOnlyList<StorageDrive>? Drives);

/// <summary>
/// place-status.json: how the last move went. <see cref="State"/> is running, succeeded or failed (and requested, which the
/// nest itself says while its request waits for the root helper). <see cref="OldCopy"/>: where the files were before a
/// move that worked; they stay there until the owner deletes them.
/// </summary>
public sealed record StorageMoveStatus(
    string State,
    int Percent = 0,
    string? Message = null,
    string? OldCopy = null,
    DateTimeOffset? At = null)
{
    public const string Requested = "requested";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public bool IsBusy => State is Requested or Running;
}

/// <summary>
/// The answer of <c>GET /api/storage</c> (and of a move request): where the nest keeps its files, the free space there, the
/// drives it could use instead (empty until the root helper has listed them, see <see cref="ListedAt"/>) and how the last
/// move went. <see cref="CanMove"/> is false on a nest without the helper (not Linux, or installed before it existed).
/// </summary>
public sealed record NestStorage(
    bool CanMove,
    string Current,
    string MainDisk,
    bool OnMainDisk,
    bool ByHand,
    long? Free,
    long? Total,
    IReadOnlyList<StorageDrive> Drives,
    DateTimeOffset? ListedAt = null,
    StorageMoveStatus? Status = null);

/// <summary>Body of <c>POST /api/storage/move</c>: the folder the nest's files should move to (a drive's <see cref="StorageDrive.Folder"/>).</summary>
public sealed record StorageMoveRequest(string? DataDir);
