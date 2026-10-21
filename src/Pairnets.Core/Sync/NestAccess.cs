namespace Pairnets.Core.Sync;

/// <summary>What the sync engine does with one path of a nest shared with this computer (see <see cref="NestAccess.StepFor"/>).</summary>
public enum ShareStep
{
    /// <summary>As the three-hash decision says: the owner's computers, and everything a friend may do.</summary>
    AsPlanned,

    /// <summary>
    /// "View only": a change made here (an edit, a new file, a deletion) stays on this computer. It is not sent and not
    /// retried, and the nest's later changes to it wait too, until the person picks "Put back".
    /// </summary>
    HeldBack,

    /// <summary>Outside the shared folders: the file is this computer's own and is left alone (never sent, no error).</summary>
    LocalOnly,

    /// <summary>
    /// Outside the shared folders, but synced before (the share was made smaller): the file stays here and the engine
    /// forgets it was synced, so it is never deleted for leaving the share.
    /// </summary>
    Forget,

    /// <summary>A held-back change the person chose to undo ("Put back"): the nest's version comes back, or the new file goes.</summary>
    PutBack,
}

/// <summary>
/// What this computer may do on its nest (friend invites, 1.0.61.2): <see cref="Owner"/> (the account that owns the nest;
/// every computer from before friends), <see cref="Edit"/> ("Can change") or <see cref="View"/> ("View only").
/// <see cref="Folders"/> is null for the whole nest, or the top-level folders shared: a path is inside the share when it
/// is one of them or lies under one (ordinal, exact case, as the nest checks it). <see cref="SharedBy"/> is the owner's
/// name on a nest shared with this computer, and <see cref="NestName"/> the nest's own name, for "Anna stopped sharing
/// Home nest". The nest refuses anything else anyway (403 <c>view_only</c> or <c>not_shared</c>); this keeps the app
/// from asking.
/// </summary>
public sealed record NestAccess(string Level, IReadOnlyList<string>? Folders = null, string? SharedBy = null, string? NestName = null)
{
    public const string Owner = "owner";
    public const string Edit = "edit";
    public const string View = "view";

    /// <summary>The owner's access: everything (also what an older nest, which knows no friends, means).</summary>
    public static readonly NestAccess Full = new(Owner);

    public bool IsOwner => Level == Owner;

    /// <summary>The nest belongs to someone else and was shared with this computer.</summary>
    public bool IsShared => !IsOwner;

    /// <summary>May upload, delete and rename on the nest (not "View only").</summary>
    public bool CanWrite => Level != View;

    public bool IsViewOnly => Level == View;

    /// <summary>
    /// Reads what a nest or the service said. A missing level is the owner's (an own nest, or an older nest); a level this
    /// app does not know is "View only", so the app never does more than it understands. The owner never has folders;
    /// names that are not plain top-level folder names are dropped, and folders given but none usable share nothing.
    /// </summary>
    public static NestAccess Read(string? level, IEnumerable<string?>? folders, string? sharedBy = null, string? nestName = null)
    {
        var known = string.IsNullOrWhiteSpace(level) ? Owner : level.Trim() is Owner or Edit or View ? level.Trim() : View;
        if (known == Owner)
            return Full;
        IReadOnlyList<string>? shared = folders?.Where(IsFolderName).Select(f => f!).Distinct(StringComparer.Ordinal).ToList();
        return new NestAccess(known, shared, Clean(sharedBy), Clean(nestName));
    }

    /// <summary>A top-level folder name a share can name: 1–255 characters, no "/", "\" or control character, not "." or "..".</summary>
    public static bool IsFolderName(string? name) =>
        name is { Length: > 0 and <= 255 } && name is not ("." or "..")
        && !name.Contains('/', StringComparison.Ordinal) && !name.Contains('\\', StringComparison.Ordinal) && !name.Any(char.IsControl);

    /// <summary>Whether <paramref name="path"/> (nest-relative, "/"-separated, as the sync API uses it) is inside the share.</summary>
    public bool Allows(string? path)
    {
        if (Folders is null)
            return true;
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var folder in Folders)
        {
            if (string.Equals(path, folder, StringComparison.Ordinal)
                || (path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A change made here that a "View only" computer keeps to itself: the file here is neither what was last synced
    /// (<paramref name="base"/>) nor what the nest has now (<paramref name="server"/>). That is an edit, a new file or a
    /// deletion made here, also when the nest changed the same file since.
    /// </summary>
    public static bool IsHeldBack(string? local, string? server, string? @base) =>
        !string.Equals(local, @base, StringComparison.Ordinal) && !string.Equals(local, server, StringComparison.Ordinal);

    /// <summary>
    /// What happens to <paramref name="path"/>, which the three-hash decision would give <paramref name="action"/>, on a
    /// nest with this access. <paramref name="putBack"/>: the person chose "Put back" for the held-back changes.
    /// </summary>
    public ShareStep StepFor(string path, SyncAction action, string? local, string? server, string? @base, bool putBack)
    {
        if (IsOwner || action == SyncAction.None)
            return ShareStep.AsPlanned;
        if (!Allows(path))
        {
            return action switch
            {
                SyncAction.RecordBase or SyncAction.ClearBase => ShareStep.AsPlanned, // only this computer's notes
                SyncAction.DeleteLocal => ShareStep.Forget,
                _ => ShareStep.LocalOnly,
            };
        }
        if (CanWrite || !IsHeldBack(local, server, @base))
            return ShareStep.AsPlanned;
        return putBack ? ShareStep.PutBack : ShareStep.HeldBack;
    }

    public bool Equals(NestAccess? other) =>
        other is not null && Level == other.Level && SharedBy == other.SharedBy && NestName == other.NestName
        && (Folders is null ? other.Folders is null : other.Folders is not null && Folders.SequenceEqual(other.Folders, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Level, SharedBy, NestName, Folders is null ? 0 : string.Join('/', Folders).GetHashCode(StringComparison.Ordinal));

    private static string? Clean(string? text) => Client.ComputerName.Clean(text) is { Length: > 0 } clean ? clean : null;
}
