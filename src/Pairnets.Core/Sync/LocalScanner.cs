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

    /// <summary>
    /// The hashes made so far are saved after this many files, or <see cref="SaveEvery"/>, and when the scan is stopped
    /// (Pause, closing the app). So reading a big folder is never started over: the next scan only reads what is left.
    /// </summary>
    public int SaveBatch { get; init; } = 250;

    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often <see cref="Progress"/> is told at most.</summary>
    public TimeSpan ReportEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Told how far the scan is (at the start, then at most every <see cref="ReportEvery"/>).</summary>
    public Action<ScanProgress>? Progress { get; set; }

    public async Task<ScanResult> ScanAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
            throw new ScanFailedException($"Sync folder '{_root}' does not exist.");

        var result = new ScanResult();
        var known = _state.LoadFiles();
        var tally = new Tally(this);
        tally.Report(force: true);
        try
        {
            await WalkAsync(result, known, tally, ct).ConfigureAwait(false);
        }
        catch when (tally.Unsaved > 0)
        {
            // Stopped part-way (Pause, an error): keep what was read, so the next scan does not read it again.
            try
            {
                tally.Save();
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
            {
                _log.LogDebug("Could not keep the hashes of a stopped scan: {Error}", ex.Message);
            }
            throw;
        }

        tally.Save();
        _state.PruneCacheRows(result.Files.Keys.ToHashSet(StringComparer.Ordinal));
        return result;
    }

    /// <summary>The counts of one scan, the hashes not saved yet, and when it last saved and reported.</summary>
    private sealed class Tally(LocalScanner owner)
    {
        private readonly List<(string, long, long, string, long)> _unsaved = [];
        private long _savedAt = owner._clock.GetTimestamp();
        private long _reportedAt;

        public int Checked;
        public int Read;
        public string? Reading;
        public long ReadingBytes;
        public long ReadingSize;

        public int Unsaved => _unsaved.Count;

        public void Add((string, long, long, string, long) row)
        {
            _unsaved.Add(row);
            if (_unsaved.Count >= owner.SaveBatch || owner._clock.GetElapsedTime(_savedAt) >= owner.SaveEvery)
                Save();
        }

        public void Save()
        {
            if (_unsaved.Count > 0)
                owner._state.SetCaches(_unsaved);
            _unsaved.Clear();
            _savedAt = owner._clock.GetTimestamp();
        }

        public void Report(bool force = false)
        {
            if (owner.Progress is not { } progress)
                return;
            var now = owner._clock.GetTimestamp();
            if (!force && _reportedAt != 0 && owner._clock.GetElapsedTime(_reportedAt, now) < owner.ReportEvery)
                return;
            _reportedAt = now;
            progress(new ScanProgress(Checked, Read, Reading, ReadingBytes, ReadingSize));
        }
    }

    private async Task WalkAsync(ScanResult result, Dictionary<string, FileState> known, Tally tally, CancellationToken ct)
    {
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

                var file = await InspectFileAsync((FileInfo)entry, rel, known, tally, ct).ConfigureAwait(false);
                result.Files[rel] = file;
                tally.Checked++;
                tally.Report();
            }
        }
        tally.Report(force: true);
    }

    private async Task<LocalFile> InspectFileAsync(
        FileInfo info,
        string rel,
        Dictionary<string, FileState> known,
        Tally tally,
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
        tally.Read++;
        tally.Reading = rel;
        tally.ReadingBytes = 0;
        tally.ReadingSize = size;
        tally.Report();
        try
        {
            (hash, _) = await ContentHash.OfFileAsync(info.FullName, read =>
            {
                tally.ReadingBytes = read;
                tally.Report();
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by another program, or vanished: unknown this pass, never "deleted".
            return new LocalFile(rel, size, mtime, null, false, "unreadable: " + ex.Message);
        }
        finally
        {
            tally.Reading = null;
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

        tally.Add((rel, size, mtime, hash, _clock.GetUtcNow().UtcTicks));
        return new LocalFile(rel, size, mtime, hash, true, null);
    }
}
