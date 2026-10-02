using Tether.Core.Sync;

namespace Tether.Core.Client;

/// <summary>Everything a UI needs to draw the current state, as one immutable value.</summary>
public sealed record StatusSnapshot(
    RunnerStatus Status,
    string Text,
    DateTimeOffset? LastSyncAt,
    string? CurrentPath,
    string? Operation,
    long BytesDone,
    long BytesTotal,
    int FilesDone,
    int FilesTotal,
    BlockReason BlockReason,
    int PendingDeletes,
    int Warnings,
    bool Paused)
{
    public static StatusSnapshot Initial { get; } =
        new(RunnerStatus.Offline, "Starting…", null, null, null, 0, 0, 0, 0, BlockReason.None, 0, 0, false);

    /// <summary>0–100 for the current file, or null when unknown (no size yet / not transferring).</summary>
    public int? Percent => CurrentPath is not null && BytesTotal > 0 ? (int)Math.Clamp(BytesDone * 100 / BytesTotal, 0, 100) : null;

    public bool IsTransferring => Status == RunnerStatus.Syncing && CurrentPath is not null;

    /// <summary>"64.0 MB of 100 MB · 64%" (or just the size while the total is unknown).</summary>
    public string ProgressText => BytesTotal > 0
        ? $"{Format.Bytes(BytesDone)} of {Format.Bytes(BytesTotal)} · {Percent}%"
        : BytesDone > 0 ? Format.Bytes(BytesDone) : string.Empty;

    /// <summary>"File 3 of 5".</summary>
    public string FileCountText => FilesTotal > 0 ? $"File {Math.Min(FilesDone + 1, FilesTotal)} of {FilesTotal}" : string.Empty;

    /// <summary>File name of the current transfer.</summary>
    public string? CurrentFileName => CurrentPath is null ? null : Paths.PathRules.FileName(CurrentPath);

    /// <summary>Folder of the current transfer ("" for the top level).</summary>
    public string CurrentFolder => CurrentPath is null ? string.Empty : Paths.PathRules.Parent(CurrentPath) ?? string.Empty;

    /// <summary>"Uploading", "Downloading", "Deleting" or "Working on".</summary>
    public string OperationText => Operation switch
    {
        "upload" => "Uploading",
        "download" => "Downloading",
        "delete" => "Deleting",
        _ => "Working on",
    };

    public string LastSyncText => LastSyncAt is { } at ? "Last synced " + at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "Not synced yet";

    /// <summary>Short status line shown under the title.</summary>
    public string Headline => Status switch
    {
        RunnerStatus.Idle => "Up to date",
        RunnerStatus.Syncing => "Syncing…",
        RunnerStatus.Offline => "Offline",
        RunnerStatus.Paused => "Paused",
        RunnerStatus.Blocked => "Needs your decision",
        _ => "Problem",
    };

    /// <summary>Extra detail under the headline, without repeating it ("Offline: timeout" → "timeout").</summary>
    public string DetailText
    {
        get
        {
            var root = Headline.TrimEnd('…', '.');
            var text = Text.Trim();
            if (text.Length == 0 || string.Equals(text.TrimEnd('…', '.'), root, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (text.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase))
                return text[(root.Length + 1)..].Trim();
            return text;
        }
    }

    /// <summary>The action the UI should offer for a blocked pass, or null.</summary>
    public string? FixLabel => Status != RunnerStatus.Blocked ? null : BlockReason switch
    {
        BlockReason.MassDelete or BlockReason.FolderEmpty => $"Allow these deletions ({PendingDeletes})…",
        BlockReason.FolderMissing or BlockReason.MarkerMissing or BlockReason.MarkerMismatch => "Locate the sync folder…",
        BlockReason.ForeignMarker => "Confirm this folder…",
        BlockReason.ServerChanged or BlockReason.ServerRolledBack => "Re-link to this server…",
        _ => null,
    };
}
