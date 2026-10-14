using System.Globalization;

namespace Pairnets.Core.Client;

/// <summary>Where one file of the current sync is.</summary>
public enum BatchFileState
{
    Waiting,
    Moving,
    Done,
}

/// <summary>One file of the current sync: done, moving now (with its percent) or still waiting its turn.</summary>
public sealed record BatchFile(string Path, string Operation, BatchFileState State, int? Percent = null)
{
    public string FileName => Paths.PathRules.FileName(Path);

    public bool IsUpload => Operation == "upload";

    public bool IsDone => State == BatchFileState.Done;

    public bool IsMoving => State == BatchFileState.Moving;

    public bool IsWaiting => State == BatchFileState.Waiting;

    public bool IsMovingUp => IsMoving && IsUpload;

    public bool IsMovingDown => IsMoving && !IsUpload;

    /// <summary>"Uploaded", "78%" or "Waiting".</summary>
    public string StateText => State switch
    {
        BatchFileState.Done => IsUpload ? "Uploaded" : "Downloaded",
        BatchFileState.Moving => Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : IsUpload ? "Uploading" : "Downloading",
        _ => "Waiting",
    };
}

/// <summary>
/// A row in "In progress": one file, or a folder holding several files of this sync. A folder row opens to list
/// every file of the sync in that folder (done, moving or waiting).
/// </summary>
public sealed record TransferGroup(string Key, string Title, string? Folder, bool IsFolder, string Detail, int? Percent, bool IsUpload, IReadOnlyList<BatchFile> Files)
{
    public string PercentText => Percent is { } p ? p.ToString(CultureInfo.InvariantCulture) + "%" : string.Empty;

    public double PercentValue => Percent ?? 0;

    public bool HasPercent => Percent is not null;
}

/// <summary>Builds the "In progress" rows from the status.</summary>
public static class TransferGroups
{
    /// <summary>
    /// Files of the sync grouped by folder: a folder with more than one file becomes one folder row. Rows with
    /// something moving come first; folders whose files are all done drop out. Without the sync's file list
    /// (older engines, or the moment a sync starts) the files moving right now are used.
    /// </summary>
    public static IReadOnlyList<TransferGroup> Build(StatusSnapshot s, int max = 4)
    {
        IReadOnlyList<BatchFile> files = s.Batch.Count > 0
            ? s.Batch
            : s.Active.Select(a => new BatchFile(a.Path, a.Operation, BatchFileState.Moving, a.Percent)).ToList();
        if (files.Count == 0)
            return [];

        var groups = new List<(TransferGroup Row, int FirstMoving)>();
        var order = 0;
        foreach (var folder in files.GroupBy(f => Paths.PathRules.Parent(f.Path) ?? string.Empty, StringComparer.Ordinal))
        {
            var list = folder.ToList();
            var position = order++;
            if (list.All(f => f.IsDone))
                continue;
            var firstMoving = list.FindIndex(f => f.IsMoving);
            var rank = firstMoving < 0 ? int.MaxValue : position;
            if (list.Count == 1 || folder.Key.Length == 0)
            {
                foreach (var f in list.Where(f => !f.IsDone))
                {
                    var percent = f.IsMoving ? f.Percent : 0;
                    groups.Add((new TransferGroup(f.Path, f.FileName, Paths.PathRules.Parent(f.Path), false,
                        Paths.PathRules.Parent(f.Path) ?? string.Empty, percent, f.IsUpload, [f]), f.IsMoving ? position : int.MaxValue));
                }
                continue;
            }
            var done = list.Count(f => f.IsDone);
            var progress = (done * 100.0 + list.Where(f => f.IsMoving).Sum(f => f.Percent ?? 0)) / list.Count;
            var uploads = list.Count(f => f.IsUpload);
            groups.Add((new TransferGroup(folder.Key, folder.Key, folder.Key, true,
                $"{done} of {list.Count} files", (int)Math.Clamp(Math.Round(progress), 0, 100), uploads * 2 >= list.Count, list), rank));
        }
        return groups
            .Select((g, i) => (g.Row, g.FirstMoving, i))
            .OrderBy(g => g.FirstMoving)
            .ThenBy(g => g.i)
            .Take(max)
            .Select(g => g.Row)
            .ToList();
    }
}
