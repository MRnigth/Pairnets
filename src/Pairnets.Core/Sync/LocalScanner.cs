using Microsoft.Extensions.Logging;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Core.State;

namespace Pairnets.Core.Sync;

/// <summary>A file found by the scanner. When <see cref="Stable"/> is false its content is unknown this pass.</summary>
public sealed record LocalFile(string Path, long Size, long MtimeTicks, string? Hash, bool Stable, string? UnstableReason);

/// <summary>Result of one scan of the sync folder.</summary>
public sealed class ScanResult
{
    public Dictionary<string, LocalFile> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Local files whose names can never be synced (rejected by <see cref="PathRules"/>).</summary>
    public List<(string Path, PathProblem Problem)> InvalidNames { get; } = [];

    /// <summary>Folders that could not be listed: everything below them is unknown, never "deleted".</summary>
    public List<string> UnknownPrefixes { get; } = [];

    /// <summary>Number of non-ignored files present (stable or not).</summary>
    public int FileCount { get; set; }

    public int UnstableCount => Files.Values.Count(f => !f.Stable);

    public bool IsUnderUnknownPrefix(string path)
    {
        foreach (var prefix in UnknownPrefixes)
        {
            if (prefix.Length == 0 || path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>Thrown when the folder cannot be scanned reliably; the pass must not continue.</summary>
public sealed class ScanFailedException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Walks the sync folder and computes content hashes. Never follows symlinks, junctions or other
/// reparse points. Files that are too fresh, locked, or changing are reported as unstable.
/// </summary>
public sealed class LocalScanner
{
    private readonly string _root;
    private readonly IgnoreList _ignore;
    private readonly StateDb _state;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public LocalScanner(string root, IgnoreList ignore, StateDb state, TimeProvider clock, ILogger log)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _ignore = ignore;
        _state = state;
        _clock = clock;
        _log = log;
    }

    /// <summary>Files modified more recently than this are skipped this pass (still being written).</summary>
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>See <see cref="FileState.CacheValidFor"/>.</summary>
    public TimeSpan RacyWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often progress is reported while files are checked (tests: every file).</summary>
    internal TimeSpan ReportEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Fingerprints are saved at least this often during a long check, so a pause or a crash loses little.</summary>
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    private const int SaveAfterFiles = 500;

    /// <summary>
    /// Lists the whole folder first (quick), then looks at every file: most are answered by the fingerprint saved last
    /// time, new or changed ones are read. <paramref name="progress"/> gets (files checked, files in the folder) right
    /// after the listing and then a few times a second. Fingerprints are saved along the way and when the check is
    /// cancelled (Pause), so the next check goes on where this one stopped.
    /// </summary>
    public async Task<ScanResult> ScanAsync(CancellationToken ct, Action<int, int>? progress = null)
    {
        if (!Directory.Exists(_root))
            throw new ScanFailedException($"Sync folder '{_root}' does not exist.");

        var result = new ScanResult();
        var known = _state.LoadFiles();
        var candidates = List(result, ct);

        progress?.Invoke(0, candidates.Count);
        var cacheUpdates = new List<(string, long, long, string, long)>();
        var lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
        var lastSave = lastReport;
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (info, rel) = candidates[i];
                result.Files[rel] = await InspectFileAsync(info, rel, known, cacheUpdates, ct).ConfigureAwait(false);
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (cacheUpdates.Count >= SaveAfterFiles || (cacheUpdates.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastSave, now) >= SaveEvery))
                {
                    _state.SetCaches(cacheUpdates);
                    cacheUpdates.Clear();
                    lastSave = now;
                }
                if (progress is not null && (i + 1 == candidates.Count || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport, now) >= ReportEvery))
                {
                    progress(i + 1, candidates.Count);
                    lastReport = now;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Paused half way: keep what was read, so the check after Resume starts where this one stopped.
            if (cacheUpdates.Count > 0)
                _state.SetCaches(cacheUpdates);
            throw;
        }

        if (cacheUpdates.Count > 0)
            _state.SetCaches(cacheUpdates);
        _state.PruneCacheRows(result.Files.Keys.ToHashSet(StringComparer.Ordinal));
        return result;
    }

    /// <summary>Walks the folder (never through links) and returns the files to look at; the rest goes into <paramref name="result"/>.</summary>
    private List<(FileInfo Info, string Rel)> List(ScanResult result, CancellationToken ct)
    {
        var candidates = new List<(FileInfo, string)>();
        var pending = new Stack<string>();
        pending.Push(string.Empty);
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var relDir = pending.Pop();
            var fullDir = relDir.Length == 0 ? _root : Path.Combine(_root, PathRules.ToOsRelative(relDir));
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(fullDir).EnumerateFileSystemInfos("*", enumOptions).ToList();
            }
            catch (UnauthorizedAccessException ex)
            {
                if (relDir.Length == 0)
                    throw new ScanFailedException($"Cannot list the sync folder: {ex.Message}", ex);
                _log.LogWarning("Cannot list folder {Folder}; its files are treated as unknown: {Error}", relDir, ex.Message);
                result.UnknownPrefixes.Add(relDir);
                continue;
            }
            catch (DirectoryNotFoundException ex) when (relDir.Length > 0)
            {
                // Folder vanished between listing its parent and now: it is in flux, not deleted.
                _log.LogDebug("Folder {Folder} vanished during scan: {Error}", relDir, ex.Message);
                result.UnknownPrefixes.Add(relDir);
                continue;
            }
            catch (IOException ex)
            {
                throw new ScanFailedException($"Cannot list folder '{relDir}': {ex.Message}", ex);
            }

            foreach (var entry in entries)
            {
                var rel = relDir.Length == 0 ? entry.Name : relDir + "/" + entry.Name;
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (IOException)
                {
                    result.UnknownPrefixes.Add(rel);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                {
                    _log.LogDebug("Skipping reparse point/symlink {Path}", rel);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!_ignore.IsIgnoredDirectory(rel))
                        pending.Push(rel);
                    continue;
                }

                if (_ignore.IsIgnored(rel))
                    continue;

                result.FileCount++;
                var problem = PathRules.Check(rel);
                if (problem != PathProblem.None)
                {
                    result.InvalidNames.Add((rel, problem));
                    continue;
                }
                candidates.Add(((FileInfo)entry, rel));
            }
        }
        return candidates;
    }

    private async Task<LocalFile> InspectFileAsync(
        FileInfo info,
        string rel,
        Dictionary<string, FileState> known,
        List<(string, long, long, string, long)> cacheUpdates,
        CancellationToken ct)
    {
        info.Refresh();
        long size;
        long mtime;
        try
        {
            size = info.Length;
            mtime = info.LastWriteTimeUtc.Ticks;
        }
        catch (IOException ex)
        {
            return new LocalFile(rel, -1, 0, null, false, "vanished: " + ex.Message);
        }

        var now = _clock.GetUtcNow().UtcTicks;
        var age = now - mtime;
        if (age >= 0 && age < StabilityWindow.Ticks)
            return new LocalFile(rel, size, mtime, null, false, "recently modified");

        if (known.TryGetValue(rel, out var cached) && cached.CacheValidFor(size, mtime, RacyWindow))
            return new LocalFile(rel, size, mtime, cached.Hash, true, null);

        string hash;
        try
        {
            (hash, _) = await ContentHash.OfFileAsync(info.FullName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by another program, or vanished: unknown this pass, never "deleted".
            return new LocalFile(rel, size, mtime, null, false, "unreadable: " + ex.Message);
        }

        info.Refresh();
        try
        {
            if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != mtime)
                return new LocalFile(rel, size, mtime, null, false, "changed while hashing");
        }
        catch (IOException ex)
        {
            return new LocalFile(rel, size, mtime, null, false, "changed while hashing: " + ex.Message);
        }

        cacheUpdates.Add((rel, size, mtime, hash, _clock.GetUtcNow().UtcTicks));
        return new LocalFile(rel, size, mtime, hash, true, null);
    }
}
