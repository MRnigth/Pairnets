using System.Text.Json;
using Tether.Core;
using Tether.Server.Storage;

namespace Tether.Server.Services;

/// <summary>
/// The server side of "Update server" in the apps. The server runs without root rights, so it
/// cannot replace itself: it only drops an empty request file that a root-owned systemd path unit
/// (tether-update.path) turns into one run of /opt/tether/update.sh. That script installs only the
/// newest official release, after checking its checksum, and reports back in status.json.
/// </summary>
public sealed class ServerUpdater(ServerPaths paths, SyncOptions options, TimeProvider? clock = null)
{
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long the root updater may take to pick up a request before the apps are told it never started.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    private const int LogTailLines = 200;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset? _lastRequest;

    public string UpdateDir => Path.Combine(paths.DataDir, "update");

    public string RequestFile => Path.Combine(UpdateDir, "request");

    public string StatusFile => Path.Combine(UpdateDir, "status.json");

    /// <summary>update.sh logs every run here (shown to the apps in Debug mode).</summary>
    public string LogFile => Path.Combine(UpdateDir, "update.log");

    public string LastAttemptFile => Path.Combine(UpdateDir, "last-attempt");

    /// <summary>The symlink systemctl enable creates for the path unit that starts the updater.</summary>
    private const string PathUnitLink = "/etc/systemd/system/multi-user.target.wants/tether-update.path";

    /// <summary>True when install.sh put the updater script next to the server binary.</summary>
    public bool Installed => File.Exists(options.UpdaterScript);

    public UpdaterStatus GetStatus()
    {
        if (!Installed)
            return new UpdaterStatus(false, "missing", "This server was installed before self-update existed. Update it once by hand.");
        var (status, writtenAt) = ReadStatusFile();
        var requestedAt = FileTime(RequestFile);
        if (requestedAt is { } asked)
        {
            // update.sh keeps the request file until it ends, so a status written since the request is this run's progress.
            if (status is not null && writtenAt >= asked - TimeSpan.FromSeconds(1))
                return status;
            if (_clock.GetUtcNow() - asked > StartTimeout)
                return new UpdaterStatus(true, "failed",
                    $"The updater did not start within {StartTimeout.TotalSeconds:0} seconds. On the server run: sudo systemctl enable --now tether-update.path", asked);
            return new UpdaterStatus(true, "requested", "Waiting for the updater to start.", asked);
        }
        if (status is not null && (_lastRequest is not { } last || writtenAt >= last - TimeSpan.FromSeconds(1)))
            return status;
        if (_lastRequest is not null)
            return new UpdaterStatus(true, "failed",
                "The updater stopped without reporting a result. Turn on Debug mode in the app to see its log.", _lastRequest);
        return new UpdaterStatus(true, "idle");
    }

    /// <summary>Everything the apps show in Debug mode to explain an update that did not work.</summary>
    public UpdaterDiagnostics GetDiagnostics(string? hide = null)
    {
        var (_, writtenAt) = ReadStatusFile();
        var requestedAt = FileTime(RequestFile);
        string? statusJson = null;
        try
        {
            if (File.Exists(StatusFile))
                statusJson = File.ReadAllText(StatusFile).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            statusJson = "(unreadable: " + ex.Message + ")";
        }
        DateTimeOffset? lastAttempt = null;
        try
        {
            if (File.Exists(LastAttemptFile) && long.TryParse(File.ReadAllText(LastAttemptFile).Trim(), out var unix))
                lastAttempt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
        }
        bool? pathUnit = Directory.Exists("/etc/systemd/system") ? File.Exists(PathUnitLink) : null; // null: not a systemd machine
        return new UpdaterDiagnostics(
            TetherInfo.ProductVersion,
            _clock.GetUtcNow(),
            GetStatus(),
            Installed,
            options.UpdaterScript,
            pathUnit,
            requestedAt is not null,
            requestedAt,
            _lastRequest,
            lastAttempt,
            statusJson,
            writtenAt,
            ReadLogTail(hide));
    }

    private (UpdaterStatus? Status, DateTimeOffset? WrittenAt) ReadStatusFile()
    {
        try
        {
            if (File.Exists(StatusFile))
            {
                var status = JsonSerializer.Deserialize<UpdaterStatus>(File.ReadAllText(StatusFile), TetherJson.Options);
                if (status is not null)
                    return (status with { Installed = true }, FileTime(StatusFile));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A half-written status reads as none; the next read sees the full one.
        }
        return (null, null);
    }

    private static DateTimeOffset? FileTime(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last lines of update.log, with anything that looks like the token hidden.</summary>
    private IReadOnlyList<string> ReadLogTail(string? hide)
    {
        try
        {
            if (!File.Exists(LogFile))
                return [];
            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxBytes = 64 * 1024;
            if (stream.Length > maxBytes)
                stream.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (stream.Length > maxBytes && lines.Count > 0)
                lines.RemoveAt(0); // probably cut in the middle
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            return lines.TakeLast(LogTailLines).Select(l => Redact(l, hide)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ["(the update log could not be read: " + ex.Message + ")"];
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TokenLine =
        new(@"(Token:\s*|SYNC_TOKEN=)\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string Redact(string line, string? hide)
    {
        if (!string.IsNullOrEmpty(hide))
            line = line.Replace(hide, "(hidden)", StringComparison.Ordinal);
        return TokenLine.Replace(line, "$1(hidden)");
    }

    public enum RequestOutcome { Requested, NotInstalled, TooSoon }

    /// <summary>Asks the root updater to run. The file's content is never read by it.</summary>
    public RequestOutcome Request()
    {
        if (!Installed)
            return RequestOutcome.NotInstalled;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_lastRequest is { } last && now - last < MinRequestInterval)
                return RequestOutcome.TooSoon;
            Directory.CreateDirectory(UpdateDir);
            File.WriteAllText(RequestFile, string.Empty);
            _lastRequest = now;
            return RequestOutcome.Requested;
        }
    }

    /// <summary>Free and total bytes of the disk holding the data directory, or nulls if unknown.</summary>
    public static (long? Free, long? Total) DiskSpace(string dataDir)
    {
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(dataDir));
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
