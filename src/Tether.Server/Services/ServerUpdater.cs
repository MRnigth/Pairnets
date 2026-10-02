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
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset? _lastRequest;

    public string UpdateDir => Path.Combine(paths.DataDir, "update");

    public string RequestFile => Path.Combine(UpdateDir, "request");

    public string StatusFile => Path.Combine(UpdateDir, "status.json");

    /// <summary>True when install.sh put the updater script next to the server binary.</summary>
    public bool Installed => File.Exists(options.UpdaterScript);

    public UpdaterStatus GetStatus()
    {
        if (!Installed)
            return new UpdaterStatus(false, "missing", "This server was installed before self-update existed. Update it once by hand.");
        if (File.Exists(RequestFile))
            return new UpdaterStatus(true, "requested", "Waiting for the updater to start.", _lastRequest);
        try
        {
            if (File.Exists(StatusFile))
            {
                var status = JsonSerializer.Deserialize<UpdaterStatus>(File.ReadAllText(StatusFile), TetherJson.Options);
                if (status is not null)
                    return status with { Installed = true };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A half-written status is reported as idle; the next read sees the full one.
        }
        return new UpdaterStatus(true, "idle");
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
