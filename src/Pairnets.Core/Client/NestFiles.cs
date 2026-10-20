using Microsoft.Extensions.Logging;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>Where a file of the Files page is right now on this computer.</summary>
public enum FileWhere
{
    /// <summary>Its content is here.</summary>
    Here,

    /// <summary>Shown in Explorer, its content on the nest until opened (Files On-Demand).</summary>
    IconOnly,

    /// <summary>Not on this computer at all.</summary>
    NestOnly,
}

/// <summary>One row of the Files page: a file on the nest, or a folder of them.</summary>
public sealed record NestEntry(string Path, bool IsFolder, long Size, DateTimeOffset Changed, string? Hash, FileWhere Where, FilePlacement Placement, int Files = 0)
{
    public string Name => PathRules.FileName(Path);

    /// <summary>"2.4 MB", or "12 files · 40 MB" for a folder.</summary>
    public string SizeText => IsFolder ? $"{Format.Count(Files)} {(Files == 1 ? "file" : "files")} · {Format.Bytes(Size)}" : Format.Bytes(Size);

    /// <summary>"today 10:45" (read when drawn).</summary>
    public string WhenText => Format.Moment(Changed, DateTimeOffset.Now);

    /// <summary>"On this PC", "On-Demand" or "Only on the nest": the folder's choice, or where a file is.</summary>
    public string WhereText => IsFolder
        ? NestFiles.PlacementText(Placement)
        : Where switch
        {
            FileWhere.Here => "On this PC",
            FileWhere.IconOnly => "On the nest (opens on click)",
            _ => "Only on the nest",
        };
}

/// <summary>How a change on the Files page went: done, or what the person sees instead.</summary>
public sealed record NestResult(bool Ok, string? Problem = null, bool NestFull = false, string? OpenPath = null)
{
    public static NestResult Done { get; } = new(true);

    /// <summary>The nest is (nearly) full: only opening works (the app shows this as a pop-up).</summary>
    public static NestResult Full { get; } = new(false, NestFiles.FullMessage, NestFull: true);

    public static NestResult Fail(string problem) => new(false, problem);
}

/// <summary>
/// The Files page: what is on the nest (from this computer's copy of the nest's list, so it shows also while the nest
/// can't be reached), where each folder's files live, and opening, adding, saving, renaming, moving and deleting files
/// on the nest. A file opened from the nest is a copy here that is saved back to the nest when it changes
/// (<see cref="OpenedFiles"/>). While the nest is (nearly) full only opening works.
/// </summary>
public sealed class NestFiles
{
    public const string FullMessage = "Your nest is full. You can still open files, but nothing can be added or changed until it has room.";
    public const string OfflineMessage = "Your nest can't be reached right now. Files kept on this PC still open.";

    private readonly ClientSession _session;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;

    internal NestFiles(ClientSession session, string openDir, ILogger log, TimeProvider clock)
    {
        _session = session;
        _log = log;
        _clock = clock;
        Opened = new OpenedFiles(session, openDir, log, clock);
    }

    /// <summary>Files opened from the nest, saved back when they change.</summary>
    public OpenedFiles Opened { get; }

    private string Root => _session.Engine.Root;

    /// <summary>The nest can't be reached: the list is the last one seen, opening a file on the nest fails.</summary>
    public bool NestOffline => _session.Status.Status == RunnerStatus.Offline;

    /// <summary>The nest is (nearly) full: only opening works.</summary>
    public bool NestFull => _session.Status.ServerSpaceLow;

    /// <summary>"On this PC", "On-Demand" or "Only on the nest".</summary>
    public static string PlacementText(FilePlacement placement) => placement switch
    {
        FilePlacement.OnDemand => "On-Demand",
        FilePlacement.NestOnly => "Only on the nest",
        _ => "On this PC",
    };

    /// <summary>The choices this computer offers: On-Demand only on Windows with a cloud folder.</summary>
    public static IReadOnlyList<FilePlacement> Choices { get; } = OperatingSystem.IsWindows()
        ? [FilePlacement.OnThisPc, FilePlacement.OnDemand, FilePlacement.NestOnly]
        : [FilePlacement.OnThisPc, FilePlacement.NestOnly];

    /// <summary>Where <paramref name="folder"/>'s files live (its own choice or the nearest above it).</summary>
    public FilePlacement PlacementOf(string folder) => _session.Engine.Placement.For(PlacementMap.NormalizeFolder(folder));

    // ------------------------------------------------------------------ listing

    /// <summary>The folders and files directly in <paramref name="folder"/> ("" for the top), folders first, by name.</summary>
    public IReadOnlyList<NestEntry> List(string folder)
    {
        folder = PlacementMap.NormalizeFolder(folder);
        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        var folders = new Dictionary<string, (long Size, int Files, long Changed)>(StringComparer.Ordinal);
        var files = new List<NestEntry>();
        foreach (var e in _session.State.LoadRemote().Values)
        {
            if (e.Deleted || !e.Path.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rest = e.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var sub = prefix + rest[..slash];
                folders.TryGetValue(sub, out var f);
                folders[sub] = (f.Size + e.Size, f.Files + 1, Math.Max(f.Changed, e.ModifiedMs));
                continue;
            }
            files.Add(new NestEntry(e.Path, false, e.Size, Moment(e.ModifiedMs), e.Hash, WhereNow(e.Path), PlacementOf(e.Path)));
        }
        var rows = folders
            .Select(f => new NestEntry(f.Key, true, f.Value.Size, Moment(f.Value.Changed), null, FileWhere.Here, PlacementOf(f.Key), f.Value.Files))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        rows.AddRange(files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase));
        return rows;
    }

    /// <summary>Folders on the nest ("Photos", "Photos/2024"), for the Settings page's list.</summary>
    public IReadOnlyList<string> Folders(int depth = 1)
    {
        var set = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var e in _session.State.LoadRemote().Values)
        {
            if (e.Deleted)
                continue;
            var parts = e.Path.Split('/');
            for (var i = 1; i < parts.Length && i <= depth; i++)
                set.Add(string.Join('/', parts[..i]));
        }
        return [.. set];
    }

    /// <summary>The bytes whose content is on this computer in <paramref name="folder"/>: what "Only on the nest" frees.</summary>
    public long BytesHere(string folder)
    {
        folder = PlacementMap.NormalizeFolder(folder);
        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        long bytes = 0;
        foreach (var e in _session.State.LoadRemote().Values)
        {
            if (!e.Deleted && e.Path.StartsWith(prefix, StringComparison.Ordinal) && WhereNow(e.Path) == FileWhere.Here)
                bytes += e.Size;
        }
        return bytes;
    }

    private static DateTimeOffset Moment(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, ms));

    private string? Local(string path) => PathRules.ResolveUnder(Root, path);

    private FileWhere WhereNow(string path)
    {
        if (Local(path) is not { } full || !File.Exists(full))
            return FileWhere.NestOnly;
        return CloudPlaceholders.TryGetAttributesAndTag(full, out var attributes, out _) && CloudPlaceholders.IsContentOnline(attributes)
            ? FileWhere.IconOnly
            : FileWhere.Here;
    }

    // ------------------------------------------------------------------ "Where your files live"

    /// <summary>
    /// Chooses where <paramref name="folder"/>'s files live on this computer. Saved in this computer's settings, and
    /// syncing starts again with it (the app saves and restarts as for any settings change).
    /// </summary>
    public void SetPlacement(string folder, FilePlacement placement)
    {
        folder = PlacementMap.NormalizeFolder(folder);
        var next = _session.Settings.Clone();
        var above = folder.Length == 0 ? FilePlacement.OnThisPc
            : new PlacementMap(next.Placement.Where(p => PlacementMap.NormalizeFolder(p.Key) != folder)).For(folder);
        foreach (var key in next.Placement.Keys.Where(k => PathRules.CaseKey(PlacementMap.NormalizeFolder(k)) == PathRules.CaseKey(folder)).ToList())
            next.Placement.Remove(key);
        if (placement != above)
            next.Placement[folder] = placement; // the same as the folder above: no choice of its own
        _log.LogInformation("Files in {Folder} now live: {Placement}", folder.Length == 0 ? "(all)" : folder, placement);
        _session.ChangeSettings(next);
    }

    // ------------------------------------------------------------------ opening and saving a copy

    /// <summary>
    /// Opens a file: the one here when its content is here (or Windows fetches it), otherwise a copy downloaded from the
    /// nest, which is saved back to the nest when it is changed. <see cref="NestResult.OpenPath"/> is what the app opens.
    /// </summary>
    public async Task<NestResult> OpenAsync(string path, CancellationToken ct = default)
    {
        if (WhereNow(path) != FileWhere.NestOnly && Local(path) is { } here)
            return new NestResult(true, OpenPath: here);
        if (_session.State.GetRemote(path) is not { Deleted: false, Hash: { } hash })
            return NestResult.Fail("This file is no longer on your nest.");
        try
        {
            return new NestResult(true, OpenPath: await Opened.OpenAsync(path, hash, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            return NestResult.Fail(ex is PairnetsNetworkException ? OfflineMessage : FriendlyErrors.Describe(ex));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NestResult.Fail("The file could not be opened: " + ex.Message);
        }
    }

    /// <summary>Downloads a file to <paramref name="destination"/> (a file path the person chose).</summary>
    public async Task<NestResult> SaveCopyAsync(string path, string destination, CancellationToken ct = default)
    {
        if (WhereNow(path) == FileWhere.Here && Local(path) is { } here)
        {
            File.Copy(here, destination, overwrite: true);
            return NestResult.Done;
        }
        var tmp = destination + ".pairnets-part";
        try
        {
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var result = await _session.Api.DownloadAsync(path, output, null, ct).ConfigureAwait(false);
                if (!result.Found)
                    return NestResult.Fail("This file is no longer on your nest.");
            }
            File.Move(tmp, destination, overwrite: true);
            return NestResult.Done;
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or IOException or UnauthorizedAccessException)
        {
            return NestResult.Fail(ex is PairnetsNetworkException ? OfflineMessage : FriendlyErrors.Describe(ex));
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ changes (refused while the nest is full)

    /// <summary>
    /// Adds files from this computer to <paramref name="folder"/>: uploaded straight to the nest when the folder lives
    /// there only, otherwise copied into the Pairnets folder (the sync uploads them).
    /// </summary>
    public async Task<NestResult> AddAsync(IReadOnlyList<string> localFiles, string folder, CancellationToken ct = default)
    {
        if (NestFull)
            return NestResult.Full;
        folder = PlacementMap.NormalizeFolder(folder);
        var nestOnly = PlacementOf(folder) == FilePlacement.NestOnly;
        foreach (var source in localFiles)
        {
            var path = (folder.Length == 0 ? string.Empty : folder + "/") + Path.GetFileName(source);
            if (!PathRules.IsValid(path))
                return NestResult.Fail($"\"{Path.GetFileName(source)}\" can't be synced: rename it first.");
            if (_session.State.GetRemote(path) is { Deleted: false })
                return NestResult.Fail($"\"{Path.GetFileName(source)}\" is already in this folder on your nest.");
            if (!nestOnly)
            {
                var target = Local(path)!;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: false);
                continue;
            }
            try
            {
                await using var stream = ContentHash.OpenForSharedRead(source);
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(source)).ToUnixTimeMilliseconds();
                var (result, _, _) = await _session.Api.UploadAsync(path, ContentHash.NoneBase, mtime, stream, null, ct).ConfigureAwait(false);
                if (result.Outcome != ApiOutcome.Ok)
                    return NestResult.Fail(result.Message ?? $"Your nest did not take \"{Path.GetFileName(source)}\".");
                _session.State.UpsertRemote(result.Entry!);
            }
            catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
            {
                return NestResult.Fail(ex is PairnetsNetworkException ? OfflineMessage : FriendlyErrors.Describe(ex));
            }
        }
        _session.Runner.RequestSync("files added from the Files page");
        return NestResult.Done;
    }

    /// <summary>Renames a file or folder (within its folder).</summary>
    public Task<NestResult> RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName.Contains('/') || newName.Contains('\\'))
            return Task.FromResult(NestResult.Fail("A name can't be empty or contain / or \\."));
        var parent = PathRules.Parent(path);
        return MoveToAsync(path, parent is null ? newName : parent + "/" + newName, ct);
    }

    /// <summary>Moves a file or folder into <paramref name="toFolder"/> ("" for the top).</summary>
    public Task<NestResult> MoveAsync(string path, string toFolder, CancellationToken ct = default)
    {
        toFolder = PlacementMap.NormalizeFolder(toFolder);
        var name = PathRules.FileName(path);
        return MoveToAsync(path, toFolder.Length == 0 ? name : toFolder + "/" + name, ct);
    }

    private async Task<NestResult> MoveToAsync(string from, string to, CancellationToken ct)
    {
        if (NestFull)
            return NestResult.Full;
        if (string.Equals(from, to, StringComparison.Ordinal))
            return NestResult.Done;
        if (!PathRules.IsValid(to))
            return NestResult.Fail("That name can't be synced.");
        if (to.StartsWith(from + "/", StringComparison.Ordinal))
            return NestResult.Fail("A folder can't be moved into itself.");
        var moves = FilesUnder(from).Select(e => (e, Target: to + e.Path[from.Length..])).ToList();
        if (moves.Count == 0)
            return NestResult.Fail("This is no longer on your nest.");
        var caseOnly = PathRules.CaseKey(from) == PathRules.CaseKey(to);
        try
        {
            foreach (var (entry, target) in moves)
            {
                // Only the letter case changes: through a passing name, as the nest keeps names unique regardless of case.
                if (caseOnly)
                {
                    var step = target + ".pairnets-rename";
                    var first = await MoveOneAsync(entry.Path, step, entry.Hash!, ct).ConfigureAwait(false);
                    if (!first.Ok)
                        return first;
                    var second = await MoveOneAsync(step, target, entry.Hash!, ct).ConfigureAwait(false);
                    if (!second.Ok)
                        return second;
                    continue;
                }
                var moved = await MoveOneAsync(entry.Path, target, entry.Hash!, ct).ConfigureAwait(false);
                if (!moved.Ok)
                    return moved;
            }
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            return NestResult.Fail(ex is PairnetsNetworkException ? OfflineMessage : FriendlyErrors.Describe(ex));
        }
        finally
        {
            _session.Runner.RequestSync("moved on the Files page");
        }
        _session.Activity.Add(ActivityKind.Info, to, $"Moved {PathRules.FileName(from)} to {to}", _clock);
        return NestResult.Done;
    }

    private async Task<NestResult> MoveOneAsync(string from, string to, string hash, CancellationToken ct)
    {
        var result = await _session.Api.MoveAsync(from, to, hash, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ApiOutcome.Ok:
                _session.State.UpsertRemote(result.Entry!);
                if (_session.State.GetRemote(from) is { } old)
                    _session.State.UpsertRemote(old with { Deleted = true, Hash = null });
                FollowHere(from, to);
                return NestResult.Done;
            case ApiOutcome.NotSupported:
                return NestResult.Fail(result.Message!);
            case ApiOutcome.Conflict:
                return NestResult.Fail(result.Message ?? $"\"{PathRules.FileName(to)}\" can't be used: the file changed, or the name is taken.");
            default:
                return NestResult.Fail(result.Message ?? "Your nest did not move it.");
        }
    }

    /// <summary>
    /// The copy here follows a move on the nest: a file here moves along (the next sync records it), a placeholder is
    /// removed (the next sync places one at the new name).
    /// </summary>
    private void FollowHere(string from, string to)
    {
        if (Local(from) is not { } source || !File.Exists(source) || Local(to) is not { } target)
            return;
        try
        {
            if (WhereNow(from) == FileWhere.IconOnly)
            {
                _session.State.RemoveFile(from);
                File.Delete(source);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            _session.State.RemoveFile(from);
            File.Move(source, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogInformation("The copy of {Path} here stays where it is: {Error}", from, ex.Message);
        }
    }

    /// <summary>Deletes a file or folder on the nest (its versions go to History) and the copy here (to the trash).</summary>
    public async Task<NestResult> DeleteAsync(string path, CancellationToken ct = default)
    {
        if (NestFull)
            return NestResult.Full;
        var files = FilesUnder(path);
        if (files.Count == 0)
            return NestResult.Fail("This is no longer on your nest.");
        try
        {
            foreach (var entry in files)
            {
                var result = await _session.Api.DeleteAsync(entry.Path, entry.Hash!, ct).ConfigureAwait(false);
                if (result.Outcome == ApiOutcome.Conflict)
                    return NestResult.Fail($"\"{entry.Name}\" changed on your nest meanwhile; it was not deleted.");
                if (result.Entry is not null)
                    _session.State.UpsertRemote(result.Entry);
                if (Local(entry.Path) is { } here && File.Exists(here))
                {
                    _session.State.RemoveFile(entry.Path);
                    _session.Trash.Delete(here);
                }
            }
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException or IOException or UnauthorizedAccessException)
        {
            return NestResult.Fail(ex is PairnetsNetworkException ? OfflineMessage : FriendlyErrors.Describe(ex));
        }
        finally
        {
            _session.Runner.RequestSync("deleted on the Files page");
        }
        _session.Activity.Add(ActivityKind.DeletedOnServer, path, $"Deleted on the nest: {path} (it can be restored from History)", _clock);
        return NestResult.Done;
    }

    /// <summary>The live files at <paramref name="path"/>: the file itself, or every file in the folder.</summary>
    private List<NestEntry> FilesUnder(string path) => _session.State.LoadRemote().Values
        .Where(e => !e.Deleted && e.Hash is not null && (e.Path == path || e.Path.StartsWith(path + "/", StringComparison.Ordinal)))
        .OrderBy(e => e.Path, StringComparer.Ordinal)
        .Select(e => new NestEntry(e.Path, false, e.Size, Moment(e.ModifiedMs), e.Hash, FileWhere.NestOnly, FilePlacement.OnThisPc))
        .ToList();
}
