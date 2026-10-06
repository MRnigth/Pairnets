using Tether.Core.Paths;

namespace Tether.Core.Client;

/// <summary>
/// A file as the server knows it, for the History page: a current file, or a deleted one (whose
/// <see cref="ChangedAt"/> is when it was deleted, by the server's clock).
/// </summary>
public sealed record ServerFile(string Path, bool Deleted, DateTimeOffset ChangedAt, long Size, string? Hash = null)
{
    public static ServerFile From(ManifestEntry e) =>
        new(e.Path, e.Deleted, DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, e.ModifiedMs)), e.Size, e.Hash);

    public string Name => PathRules.FileName(Path);

    /// <summary>The folder it is in, or "Top folder".</summary>
    public string Folder => PathRules.Parent(Path) ?? "Top folder";

    /// <summary>"Deleted 2 days ago · Projects" or "1.2 MB · changed 5 min ago · Projects" (read when drawn).</summary>
    public string Secondary
    {
        get
        {
            var when = Format.RelativeTime(ChangedAt, DateTimeOffset.Now);
            return Deleted ? $"Deleted {when} · {Folder}" : $"{Format.Bytes(Size)} · changed {when} · {Folder}";
        }
    }
}

/// <summary>A stored older version in the History page's detail panel.</summary>
public sealed record VersionRow(string Path, HistoryVersion Version, string Title, string Detail)
{
    /// <summary>
    /// History keeps a version from the moment something replaced it (or deleted it), so that is
    /// the time shown: "Today 14:02" with "Replaced · 1.2 MB", or for a deleted file's newest
    /// copy "Deleted · 1.2 MB".
    /// </summary>
    public static IReadOnlyList<VersionRow> For(ServerFile file, IReadOnlyList<HistoryVersion> versions, DateTimeOffset now)
    {
        var rows = new List<VersionRow>();
        for (var i = 0; i < versions.Count; i++)
        {
            var v = versions[i];
            var moment = Format.Moment(v.StoredAtUtc, now);
            var verb = file.Deleted && i == 0 ? "Deleted" : "Replaced";
            var same = file.Hash is { } h && Hashing.ContentHash.Short(h) == v.Hash8;
            rows.Add(new VersionRow(file.Path, v, char.ToUpperInvariant(moment[0]) + moment[1..],
                $"{verb} · {Format.Bytes(v.Size)}" + (same ? " · same content as now" : string.Empty)));
        }
        return rows;
    }
}

/// <summary>Where the History page gets its data: the running session (or sample data in tests).</summary>
public interface IHistorySource
{
    Task<IReadOnlyList<ServerFile>> GetServerFilesAsync(CancellationToken ct);

    Task<IReadOnlyList<HistoryVersion>> GetVersionsAsync(string path, CancellationToken ct);

    /// <summary>Null on success, otherwise what went wrong (for the page to show).</summary>
    Task<string?> RestoreVersionAsync(string path, HistoryVersion version, CancellationToken ct);
}

/// <summary>Which files the History page lists.</summary>
public static class HistoryQuery
{
    /// <summary>How long the server keeps deleted files and older versions (its default).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>The most rows a list shows; searching narrows it down.</summary>
    public const int MaxRows = 400;

    /// <summary>
    /// Deleted files from the last <see cref="Retention"/> (newest first), or current files (most
    /// recently changed first), whose path contains <paramref name="search"/> when given.
    /// </summary>
    public static (IReadOnlyList<ServerFile> Rows, int Total) Filter(IEnumerable<ServerFile> files, bool deleted, string? search, DateTimeOffset now)
    {
        var term = search?.Trim();
        var matches = files
            .Where(f => f.Deleted == deleted && (!deleted || now - f.ChangedAt <= Retention))
            .Where(f => string.IsNullOrEmpty(term) || f.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.ChangedAt)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        return (matches.Take(MaxRows).ToList(), matches.Count);
    }

    /// <summary>"12 deleted files", "Showing 400 of 1,204 files: search to find one", "No deleted files in the last 30 days".</summary>
    public static string Summary(int shown, int total, bool deleted, bool searching)
    {
        var noun = deleted ? "deleted file" : "file";
        if (total == 0)
            return searching ? "Nothing matches your search" : deleted ? "No files were deleted in the last 30 days" : "The server has no files yet";
        var count = total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        if (shown < total)
            return $"Showing {shown} of {count} {noun}s · search to find one";
        return total == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }
}
