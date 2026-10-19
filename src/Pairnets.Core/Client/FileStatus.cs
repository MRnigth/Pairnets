using Pairnets.Core.Paths;
using Pairnets.Core.State;

namespace Pairnets.Core.Client;

/// <summary>Where one file or folder stands, as the file manager shows it (a tick, arrows, a red cross).</summary>
public enum FileSyncState
{
    /// <summary>The same here and on the nest.</summary>
    Synced,

    /// <summary>Moving right now, or next in the current sync.</summary>
    Syncing,

    /// <summary>Changed here (or on the nest) and not synced yet: the next sync takes it.</summary>
    Waiting,

    /// <summary>Pairnets cannot sync it; <see cref="FileSyncStatus.Text"/> says why.</summary>
    Problem,

    /// <summary>Never synced on purpose: Pairnets' own files, desktop.ini, temp files, the ignore list.</summary>
    Ignored,

    /// <summary>Not in the Pairnets folder, or Pairnets is not running a sync session.</summary>
    Unknown,
}

/// <summary>One file's or folder's status, with a plain sentence for people.</summary>
public sealed record FileSyncStatus(string SyncPath, FileSyncState State, string Text)
{
    public bool IsFolder { get; init; }
}

/// <summary>
/// Works out <see cref="FileSyncStatus"/> from what Pairnets already keeps: the sync notes (last agreed version, the
/// nest's list, path warnings) and the current sync's file list. Nothing is read from the file itself, so asking is
/// cheap and never wakes a file that only lives on the nest.
/// </summary>
public static class FileStatusReader
{
    /// <summary>
    /// The status of <paramref name="fullPath"/> inside <paramref name="root"/>. Folders get the worst of what is
    /// inside them (a problem, then syncing, then waiting).
    /// </summary>
    public static FileSyncStatus For(string root, string fullPath, StateDb state, IgnoreList ignore, StatusSnapshot status)
    {
        if (ToSyncPath(root, fullPath) is not { } rel)
            return new FileSyncStatus(fullPath, FileSyncState.Unknown, "Not in your Pairnets folder.");
        if (rel.Length == 0 || Directory.Exists(fullPath))
            return ForFolder(rel, state, ignore, status);
        if (ignore.IsIgnored(rel) || PathRules.Check(rel) == PathProblem.ReservedName)
            return new FileSyncStatus(rel, FileSyncState.Ignored, "Not synced: Pairnets skips this kind of file.");
        return ForFile(rel, fullPath, state, status);
    }

    /// <summary>"Photos/Beach.jpg" for a path inside the folder, "" for the folder itself, null for anything outside it.</summary>
    public static string? ToSyncPath(string root, string fullPath)
    {
        string full;
        string top;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
            top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(full, top, comparison))
            return string.Empty;
        var inside = top.EndsWith(Path.DirectorySeparatorChar) ? top : top + Path.DirectorySeparatorChar; // a drive root ends in one already
        if (!full.StartsWith(inside, comparison))
            return null;
        return full[inside.Length..].Replace(Path.DirectorySeparatorChar, '/');
    }

    private static FileSyncStatus ForFile(string rel, string fullPath, StateDb state, StatusSnapshot status)
    {
        if (state.LoadWarnings().TryGetValue(rel, out var warning))
            return new FileSyncStatus(rel, FileSyncState.Problem, "Can't sync: " + (warning.Message ?? warning.Code));
        if (PathRules.Check(rel) is not PathProblem.None and var problem)
            return new FileSyncStatus(rel, FileSyncState.Problem, $"Can't sync: the name cannot be synced ({problem}). Rename the file.");
        if (status.Active.FirstOrDefault(a => a.Path == rel) is { } moving)
            return new FileSyncStatus(rel, FileSyncState.Syncing, (moving.IsUpload ? "Uploading" : "Downloading")
                + (moving.BytesTotal > 0 ? $" · {moving.BytesDone * 100 / moving.BytesTotal} %" : "…"));
        if (status.Batch.FirstOrDefault(b => b.Path == rel && !b.IsDone) is { } next)
            return new FileSyncStatus(rel, FileSyncState.Syncing, next.IsUpload ? "Waiting to upload" : "Waiting to download");

        var known = state.GetFile(rel);
        var remote = state.GetRemote(rel);
        if (known?.BaseHash is null)
            return remote is { Deleted: false }
                ? new FileSyncStatus(rel, FileSyncState.Waiting, "On your nest and here, not compared yet: the next sync checks it.")
                : new FileSyncStatus(rel, FileSyncState.Waiting, "New here: the next sync uploads it.");
        if (remote is null || remote.Deleted || remote.Hash != known.BaseHash)
            return new FileSyncStatus(rel, FileSyncState.Waiting, "Changed on another computer: the next sync brings it here.");
        if (!SameAsLastSync(fullPath, known))
            return new FileSyncStatus(rel, FileSyncState.Waiting, "Changed here: the next sync uploads it.");
        return new FileSyncStatus(rel, FileSyncState.Synced, "Synced: the same here and on your nest.");
    }

    /// <summary>
    /// True when the file at <paramref name="fullPath"/> (inside <paramref name="root"/>) is the same here and on the nest,
    /// from the notes alone: what Windows' tick in Explorer stands for.
    /// </summary>
    public static bool IsSynced(string root, string fullPath, StateDb state)
    {
        if (ToSyncPath(root, fullPath) is not { Length: > 0 } rel || state.GetFile(rel) is not { BaseHash: { } baseHash } known)
            return false;
        return state.GetRemote(rel) is { Deleted: false } remote && remote.Hash == baseHash && SameAsLastSync(fullPath, known);
    }

    /// <summary>
    /// The file still has the size and time it had when Pairnets last read it, and what it read then was the synced
    /// version. Only the file's details are looked at, never its content.
    /// </summary>
    private static bool SameAsLastSync(string fullPath, FileState known)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists && known.Hash == known.BaseHash && info.Length == known.Size && info.LastWriteTimeUtc.Ticks == known.MtimeTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FileSyncStatus ForFolder(string rel, StateDb state, IgnoreList ignore, StatusSnapshot status)
    {
        var name = rel.Length == 0 ? "Your Pairnets folder" : "This folder";
        if (rel.Length > 0 && ignore.IsIgnoredDirectory(rel))
            return new FileSyncStatus(rel, FileSyncState.Ignored, "Not synced: Pairnets skips this folder.") { IsFolder = true };
        bool Inside(string path) => rel.Length == 0 || path.StartsWith(rel + "/", StringComparison.Ordinal);
        var problems = state.LoadWarnings().Keys.Count(Inside);
        if (problems > 0)
            return new FileSyncStatus(rel, FileSyncState.Problem,
                $"{name} has {problems} file{(problems == 1 ? "" : "s")} Pairnets can't sync. Open Pairnets to see why.") { IsFolder = true };
        var moving = status.Batch.Count(b => !b.IsDone && Inside(b.Path)) + status.Active.Count(a => Inside(a.Path) && status.Batch.All(b => b.Path != a.Path));
        if (moving > 0)
            return new FileSyncStatus(rel, FileSyncState.Syncing, $"Syncing {moving} file{(moving == 1 ? "" : "s")}…") { IsFolder = true };
        return new FileSyncStatus(rel, FileSyncState.Synced, $"{name} is up to date.") { IsFolder = true };
    }
}
