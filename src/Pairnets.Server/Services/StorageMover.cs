using System.Text;
using System.Text.Json;
using Pairnets.Core;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Services;

/// <summary>
/// The server side of "Where your nest keeps files" in the apps' Settings. Like <see cref="ServerUpdater"/>, the server runs
/// without root rights, so it only leaves a request in its update folder (place-request): "action=list" for the list of
/// drives, or "action=move" with the folder to move to. A root-owned path unit (pairnets-place.path) runs
/// /opt/pairnets/place.sh for it, which re-checks everything, writes drives.json and reports a move in place-status.json.
/// During a move the server is stopped near the end and started again from the new place.
/// </summary>
public sealed class StorageMover(ServerPaths paths, SyncOptions options, ServerUpdater updater, TimeProvider? clock = null)
{
    /// <summary>A drive list older than this is listed again when an app asks.</summary>
    public static readonly TimeSpan ListMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>At most one list request this often.</summary>
    public static readonly TimeSpan ListInterval = TimeSpan.FromSeconds(10);

    /// <summary>At most one move request this often.</summary>
    public static readonly TimeSpan MoveInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long the root helper may take to pick up a move request before the apps are told it never started.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// A "running" status not written for this long is from a move that ended without saying so (the machine went off, say).
    /// Copying a very big nest can take long between two percent, so this is generous.
    /// </summary>
    public static readonly TimeSpan RunningStale = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset? _lastList;
    private DateTimeOffset? _lastMove;

    public string UpdateDir => updater.UpdateDir;

    public string RequestFile => Path.Combine(UpdateDir, "place-request");

    public string DrivesFile => Path.Combine(UpdateDir, "drives.json");

    public string StatusFile => Path.Combine(UpdateDir, "place-status.json");

    /// <summary>The root helper install.sh puts next to the updater.</summary>
    public string PlaceScript => Path.Combine(Path.GetDirectoryName(options.UpdaterScript) ?? string.Empty, "place.sh");

    /// <summary>True when this nest can move its files: the path unit and place.sh were installed.</summary>
    public bool CanMove => File.Exists(options.PlacePathUnit) && File.Exists(PlaceScript);

    /// <summary>
    /// Where the files are, the drives and how the last move went. Asks the root helper for a fresh list of drives when the
    /// one on disk is missing or old (and no request waits, and none was made in the last <see cref="ListInterval"/>).
    /// </summary>
    public NestStorage Get()
    {
        var canMove = CanMove;
        var drives = ReadJson<StorageDrivesFile>(DrivesFile);
        var pending = ReadRequest();
        var status = Status(canMove, pending);
        if (canMove && pending is null && status?.IsBusy != true)
        {
            var listedAt = drives.Value?.ListedAt ?? drives.WrittenAt;
            if (listedAt is not { } at || _clock.GetUtcNow() - at > ListMaxAge)
                RequestList();
        }
        return Build(canMove, drives.Value, status);
    }

    private NestStorage Build(bool canMove, StorageDrivesFile? drives, StorageMoveStatus? status)
    {
        var current = Clean(drives?.Current) ?? paths.DataDir;
        var mainDisk = Clean(drives?.MainDisk) ?? paths.DataDir;
        var (free, total) = ServerUpdater.DiskSpace(paths.DataDir);
        return new NestStorage(canMove, current, mainDisk, SamePath(current, mainDisk), drives?.ByHand ?? false, free, total,
            drives?.Drives ?? [], drives?.ListedAt, status);
    }

    /// <summary>How the last move went, with what only the server can tell: a request that waits, or one nobody picked up.</summary>
    private StorageMoveStatus? Status(bool canMove, (string Action, DateTimeOffset At)? pending)
    {
        if (!canMove)
            return null;
        if (pending is { Action: "move", At: var asked })
        {
            if (_clock.GetUtcNow() - asked > StartTimeout)
                return new StorageMoveStatus(StorageMoveStatus.Failed, 0,
                    $"The move did not start within {StartTimeout.TotalSeconds:0} seconds. On the server run: sudo systemctl restart pairnets-place.path", null, asked);
            return new StorageMoveStatus(StorageMoveStatus.Requested, 0, "Waiting for your nest to start the move.", null, asked);
        }
        var (status, writtenAt) = ReadJson<StorageMoveStatus>(StatusFile);
        if (status is { State: StorageMoveStatus.Running } && writtenAt is { } written && _clock.GetUtcNow() - written > RunningStale)
            return status with { State = StorageMoveStatus.Failed, Message = "The move stopped without saying how it went. Your nest keeps its files where they were before." };
        return status;
    }

    public enum MoveOutcome { Requested, AlreadyThere, NotInstalled, ByHand, Busy, UnknownFolder, TooSoon }

    /// <summary>
    /// Asks the root helper to move the files to <paramref name="dataDir"/>, which must be the folder of a usable drive in
    /// drives.json (or the usual folder on the main disk). The helper checks it all again before anything changes.
    /// </summary>
    public (MoveOutcome Outcome, string Message) RequestMove(string? dataDir)
    {
        if (!CanMove)
            return (MoveOutcome.NotInstalled, "This nest can't move its files from the app. Run the install command on the server once (Linux only).");
        lock (_gate)
        {
            var drives = ReadJson<StorageDrivesFile>(DrivesFile).Value;
            if (drives?.ByHand == true)
                return (MoveOutcome.ByHand, "Your nest's files are on a drive that was set up by hand (in /etc/fstab). Change it there.");
            var pending = ReadRequest();
            if (pending is { Action: "move" } || Status(true, pending) is { IsBusy: true })
                return (MoveOutcome.Busy, "Your nest is already moving its files.");
            var folder = Clean(dataDir);
            if (folder is null)
                return (MoveOutcome.UnknownFolder, "Choose one of your nest's drives.");
            var current = Clean(drives?.Current) ?? paths.DataDir;
            if (SamePath(folder, current))
                return (MoveOutcome.AlreadyThere, "Your nest already keeps its files there.");
            var allowed = (drives?.Drives ?? []).Where(d => d.Usable).Select(d => Clean(d.Folder)).OfType<string>()
                .Append(Clean(drives?.MainDisk));
            if (drives is null || !allowed.Any(a => a is not null && SamePath(a, folder)))
                return (MoveOutcome.UnknownFolder, "That folder is not one of your nest's drives. Choose one from the list.");
            var now = _clock.GetUtcNow();
            if (_lastMove is { } last && now - last < MoveInterval)
                return (MoveOutcome.TooSoon, "A move was asked for less than a minute ago.");
            WriteRequest("action=move\ndata_dir=" + folder + "\n");
            _lastMove = now;
            return (MoveOutcome.Requested, "Your nest starts moving its files.");
        }
    }

    private void RequestList()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_lastList is { } last && now - last < ListInterval)
                return;
            if (File.Exists(RequestFile))
                return;
            try
            {
                WriteRequest("action=list\n");
                _lastList = now;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The update folder can't be written: the apps see no drives, and the next ask tries again.
            }
        }
    }

    /// <summary>Written next to it and renamed, so the helper never reads half a request.</summary>
    private void WriteRequest(string text)
    {
        Directory.CreateDirectory(UpdateDir);
        var temp = RequestFile + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, RequestFile, overwrite: true);
    }

    /// <summary>The request that waits for the root helper (its action and when it was made), or null.</summary>
    private (string Action, DateTimeOffset At)? ReadRequest()
    {
        try
        {
            var text = File.ReadAllText(RequestFile);
            var at = FileTime(RequestFile) ?? _clock.GetUtcNow();
            var action = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("action=", StringComparison.Ordinal))?["action=".Length..];
            return (action ?? "list", at);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // not there (or just picked up)
        }
    }

    private static (T? Value, DateTimeOffset? WrittenAt) ReadJson<T>(string path) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), PairnetsJson.Options);
            return (value, value is null ? null : FileTime(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null); // missing, or half written: the next read sees the whole file
        }
    }

    private static DateTimeOffset? FileTime(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            return written.Year <= 1601 ? null : new DateTimeOffset(written, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A folder as sent or listed, without a trailing slash; null when empty or not a full path.</summary>
    private static string? Clean(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        var trimmed = folder.Trim();
        if (trimmed.Length > 1)
            trimmed = trimmed.TrimEnd('/');
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('\0'))
            return null;
        return trimmed.StartsWith('/') || Path.IsPathFullyQualified(trimmed) ? trimmed : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
