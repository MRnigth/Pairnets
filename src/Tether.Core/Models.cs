using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tether.Core;

/// <summary>One row of the server manifest. <see cref="Hash"/> is null for tombstones.</summary>
public sealed record ManifestEntry(
    string Path,
    string? Hash,
    long Size,
    long ModifiedMs,
    bool Deleted,
    long Version);

/// <summary>Body of every non-2xx JSON response from the server.</summary>
public sealed record ErrorBody(string Code, string? Message = null);

/// <summary>
/// Answer of GET /api/info. <see cref="Version"/> is the manifest version (the change counter);
/// <see cref="ServerVersion"/> is the installed Tether release. The fields after
/// <see cref="ApiVersion"/> are missing on older servers.
/// </summary>
public sealed record ServerInfo(
    string ServerId,
    long Version,
    int ApiVersion,
    string? ServerVersion = null,
    long? DiskFreeBytes = null,
    long? DiskTotalBytes = null,
    UpdaterStatus? Updater = null);

/// <summary>The server's self-updater. <see cref="State"/>: missing, idle, requested, running, succeeded or failed.</summary>
public sealed record UpdaterStatus(bool Installed, string State, string? Message = null, DateTimeOffset? At = null);

/// <summary>
/// What the server knows about its self-updater (<c>GET /api/update/diagnostics</c>), shown in the apps'
/// Debug mode to explain an "Update server" that did not work.
/// </summary>
public sealed record UpdaterDiagnostics(
    string? ServerVersion,
    DateTimeOffset ServerTimeUtc,
    UpdaterStatus Status,
    bool UpdaterInstalled,
    string UpdaterScript,
    bool? PathUnitEnabled,
    bool RequestPending,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? LastRequestAt,
    DateTimeOffset? LastAttemptAt,
    string? StatusJson,
    DateTimeOffset? StatusWrittenAt,
    IReadOnlyList<string> LogTail)
{
    /// <summary>A plain-text report for the details box and the log.</summary>
    public string ToReport()
    {
        static string Time(DateTimeOffset? t) => t?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "never";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Server version:      {ServerVersion ?? "unknown"} (server clock {Time(ServerTimeUtc)})");
        sb.AppendLine($"Updater state:       {Status.State}{(Status.Message is { } m ? " - " + m : string.Empty)}");
        sb.AppendLine($"update.sh present:   {(UpdaterInstalled ? "yes" : "no")} ({UpdaterScript})");
        sb.AppendLine($"Path unit enabled:   {(PathUnitEnabled is { } on ? on ? "yes" : "NO (run: sudo systemctl enable --now tether-update.path)" : "unknown")}");
        sb.AppendLine($"Request waiting:     {(RequestPending ? "yes, since " + Time(RequestedAt) : "no")}");
        sb.AppendLine($"Last request (app):  {Time(LastRequestAt)}");
        sb.AppendLine($"Last updater run:    {Time(LastAttemptAt)}");
        sb.AppendLine($"status.json:         {StatusJson ?? "(none)"} (written {Time(StatusWrittenAt)})");
        sb.AppendLine(LogTail.Count == 0 ? "update.log:          (empty or missing; the updater has not run since Debug logging was added)" : "update.log (last lines):");
        foreach (var line in LogTail)
            sb.AppendLine("  " + line);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>One stored version of a file in the server's history/ folder.</summary>
public sealed record HistoryVersion(string Id, DateTimeOffset StoredAtUtc, long Size, string Hash8);

/// <summary>Stable machine-readable error codes used in <see cref="ErrorBody"/>.</summary>
public static class ErrorCodes
{
    public const string Conflict = "conflict";
    public const string CaseCollision = "case-collision";
    public const string InvalidName = "invalid-name";
    public const string NotFound = "not-found";
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad-request";
    public const string UpdaterMissing = "updater-missing";
}

/// <summary>HTTP header names used by Tether.</summary>
public static class TetherHeaders
{
    public const string Token = "X-Sync-Token";
    public const string DeviceId = "X-Device-Id";
    public const string ServerId = "X-Tether-Server-Id";
    public const string Version = "X-Tether-Version";
    public const string Hash = "X-Tether-Hash";
}

/// <summary>Shared JSON settings: camelCase, as used on the wire.</summary>
public static class TetherJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}
