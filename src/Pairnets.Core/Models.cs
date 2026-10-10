using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pairnets.Core;

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
/// <see cref="ServerVersion"/> is the installed Pairnets release. The fields after
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
    IReadOnlyList<string> LogTail,
    string? PathUnitState = null,
    string? ServiceState = null,
    string? RequestPath = null)
{
    /// <summary>A plain-text report for the details box and the log.</summary>
    public string ToReport()
    {
        static string Time(DateTimeOffset? t) => t?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "never";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Server version:      {ServerVersion ?? "unknown"} (server clock {Time(ServerTimeUtc)})");
        sb.AppendLine($"Updater state:       {Status.State}{(Status.Message is { } m ? " - " + m : string.Empty)}");
        sb.AppendLine($"update.sh present:   {(UpdaterInstalled ? "yes" : "no")} ({UpdaterScript})");
        sb.AppendLine($"Path unit enabled:   {(PathUnitEnabled is { } on ? on ? "yes" : "NO (run: sudo systemctl enable --now pairnets-update.path)" : "unknown")}");
        sb.AppendLine($"Path unit state:     {PathUnitState ?? "unknown"}");
        if (PathUnitState is { } state && !state.StartsWith("active", StringComparison.Ordinal))
            sb.AppendLine("  -> the updater is not watching for requests. On the server run: sudo systemctl reset-failed pairnets-update.path pairnets-update.service && sudo systemctl restart pairnets-update.path");
        sb.AppendLine($"Update service:      {ServiceState ?? "unknown"}");
        sb.AppendLine($"Request file:        {RequestPath ?? "unknown"}");
        if (RequestPath is { } path && path.StartsWith('/') && path != "/var/lib/pairnets/update/request")
            sb.AppendLine("  -> the updater only watches /var/lib/pairnets/update/request. Re-run the install command on the server to fix this.");
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

/// <summary>
/// A computer that uses this server (<c>GET /api/devices</c>, the apps' Devices tab). <see cref="Id"/> is set
/// for computers with their own key (they can be removed); computers still on the shared token have none.
/// </summary>
public sealed record DeviceInfo(
    string Name,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    bool Online,
    string? AppVersion = null,
    string? System = null,
    DateTimeOffset? LastChange = null,
    string? Id = null);

/// <summary>
/// Answer of <c>GET /api/hello</c> (no sign-in needed): "this is a Pairnets server". <see cref="PublicUrl"/> is
/// the nest's own HTTPS name when it has one; <see cref="DeviceKeys"/> says computers can get their own key.
/// </summary>
/// <remarks><see cref="SignIn"/>: new computers can ask to join and be approved on the nest's website.
/// <see cref="Methods"/> is how the owner can sign in there (null on servers from before it was told).
/// <see cref="Relay"/> is only sent by a nest linked to a Pairnets account (cloud/RELAY.md §3): the apps reach it
/// through the service then, and it has no public name of its own.</remarks>
public sealed record ServerHello(string Product, int ApiVersion, string? ServerVersion, string? PublicUrl, bool DeviceKeys, bool SignIn,
    SignInMethods? Methods = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RelayHello? Relay = null);

/// <summary>A nest in relay mode: its id at the Pairnets service and the service's address (https://sync.pairnets.app).</summary>
public sealed record RelayHello(string NestId, string ServiceUrl);

/// <summary>The ways the owner can sign in on the nest's website (the apps show matching buttons).</summary>
public sealed record SignInMethods(bool Password, bool Passkeys, bool Email, bool Google);

/// <summary>Answer of <c>GET /api/me</c>: who the server thinks this computer is.</summary>
public sealed record DeviceMe(string? Id, string Name, string Kind)
{
    public const string KindDeviceKey = "device-key";
    public const string KindSharedToken = "shared-token";
}

/// <summary>A computer's own key, handed out exactly once when its sign-in is approved.</summary>
public sealed record DeviceKeyGrant(string Id, string Name, string Key);

/// <summary>Body of PATCH /api/devices/me.</summary>
public sealed record DeviceNameRequest(string? Name);

/// <summary>POST /api/pair/start: a new computer asks to join (no sign-in needed; the owner approves it).</summary>
public sealed record PairStartRequest(string? Name, string? System = null, string? AppVersion = null);

/// <summary>
/// The answer to <see cref="PairStartRequest"/>. <see cref="Code"/> ("KQ7M-4PXD") is shown on the computer and on
/// the approval page; <see cref="PollToken"/> is secret and only used to ask for the outcome. <see cref="VerifyUrl"/>
/// is the nest's approval page (null when the nest has no website yet).
/// </summary>
public sealed record PairStartResponse(string PollToken, string Code, string? VerifyUrl, int ExpiresInSeconds, int IntervalSeconds);

/// <summary>POST /api/pair/poll.</summary>
public sealed record PairPollRequest(string? PollToken);

/// <summary>
/// The outcome so far: <see cref="Status"/> is "pending", "approved" (with this computer's own key, handed out
/// once), "denied", "expired" or "used" (the key was already collected).
/// </summary>
public sealed record PairPollResponse(string Status, string? Id = null, string? Name = null, string? Key = null)
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Used = "used";
}

/// <summary>One stored version of a file in the server's history/ folder.</summary>
public sealed record HistoryVersion(string Id, DateTimeOffset StoredAtUtc, long Size, string Hash8);

/// <summary>
/// An upload that arrives in pieces (<c>POST /api/upload</c>, <c>PUT /api/upload/{id}</c>): its id and how
/// many bytes the server has, which is where the next piece starts.
/// </summary>
public sealed record UploadStatus(string Id, long Received);

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

    /// <summary>A piece of an upload did not start where the server's copy ends (ask for the status and go on from there).</summary>
    public const string UploadOffset = "upload-offset";

    /// <summary>An upload in pieces did not match the hash it was committed with; it was dropped.</summary>
    public const string UploadMismatch = "upload-mismatch";

    /// <summary>A request body was larger than the server or a proxy in front of it accepts.</summary>
    public const string TooLarge = "too-large";

    public const string Busy = "busy";

    /// <summary>The computer's own key was removed on the nest (or by "Sign out of this computer").</summary>
    public const string DeviceRemoved = "device-removed";

    /// <summary>The shared token is turned off on the nest; only computers with their own key may sync.</summary>
    public const string SharedTokenOff = "shared-token-off";

    /// <summary>The request needs the shared token, but this computer already has its own key (or the other way round).</summary>
    public const string WrongCredential = "wrong-credential";
}

/// <summary>
/// What one computer is moving right now, as it measured it. The one source for every computer's live state in the
/// apps (speed labels, the picture's lines, "waiting for PC-1"): nothing is guessed from other signals.
/// <list type="bullet">
/// <item><see cref="UpBytesPerSecond"/>, <see cref="DownBytesPerSecond"/>: bytes really moved per second (about the last 5 s).</item>
/// <item><see cref="FilesDone"/> of <see cref="FilesTotal"/>, <see cref="BytesDone"/> of <see cref="BytesTotal"/>: its current batch.</item>
/// <item><see cref="Uploading"/>, <see cref="Downloading"/>: files in flight in that direction right now (also while stalled).</item>
/// <item><see cref="Stalled"/>: files in flight, but no byte moved for <see cref="StallAfter"/> (a broken connection, or held
/// by "too many requests").</item>
/// <item><see cref="AgeSeconds"/>: set by the server only: how old the report was when it passed it on (0 when pushed live;
/// more for an app that connects later). The receiver's "last seen" is its arrival minus this.</item>
/// </list>
/// An app sends it on the push channel (<see cref="PushNames.ReportTransfer"/>) about once a second while it transfers
/// and once, all zero, when it stops; the server passes it on to the other computers (<see cref="PushNames.PeerTransfer"/>).
/// </summary>
public sealed record TransferReport(double UpBytesPerSecond, double DownBytesPerSecond, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal,
    bool Uploading = false, bool Downloading = false, bool Stalled = false, double AgeSeconds = 0)
{
    /// <summary>"Not moving anything" (sent once when a computer stops).</summary>
    public static TransferReport Idle { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Files in flight but no byte moved for this long: stalled.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(5);

    /// <summary>The largest speed believed (100 GB/s); anything above is a broken report.</summary>
    private const double MaxSpeed = 100d * 1024 * 1024 * 1024;

    [JsonIgnore]
    public int FilesLeft => Math.Max(0, FilesTotal - FilesDone);

    [JsonIgnore]
    public long BytesLeft => Math.Max(0, BytesTotal - BytesDone);

    /// <summary>True while it moves bytes, has files in flight, or still has files to go in its batch.</summary>
    [JsonIgnore]
    public bool IsActive => Uploading || Downloading || UpBytesPerSecond >= 1 || DownBytesPerSecond >= 1 || FilesLeft > 0;

    /// <summary>The report with impossible values cut back (it comes from another computer).</summary>
    public TransferReport Sanitized()
    {
        static double Clamp(double v, double max) => double.IsFinite(v) ? Math.Clamp(v, 0, max) : 0;
        var total = Math.Clamp(FilesTotal, 0, 100_000_000);
        var bytesTotal = Math.Max(0, BytesTotal);
        return new TransferReport(Clamp(UpBytesPerSecond, MaxSpeed), Clamp(DownBytesPerSecond, MaxSpeed), Math.Clamp(FilesDone, 0, total), total,
            Math.Clamp(BytesDone, 0, bytesTotal), bytesTotal, Uploading, Downloading, Stalled, Clamp(AgeSeconds, 3600));
    }
}

/// <summary>
/// Names on the push channel (the SignalR hub at /hub) that the apps and the server share. A message keeps its
/// arguments forever (older apps fail on a mismatch); new information gets a new name. An app or server that does not
/// know a name simply never sends or answers it.
/// </summary>
public static class PushNames
{
    /// <summary>App → server: <c>ReportTransfer(TransferReport)</c>, this computer's live speeds and batch.</summary>
    public const string ReportTransfer = "ReportTransfer";

    /// <summary>Server → the other apps: <c>PeerTransfer(device name, TransferReport)</c>.</summary>
    public const string PeerTransfer = "PeerTransfer";
}

/// <summary>HTTP header names used by Pairnets.</summary>
public static class PairnetsHeaders
{
    public const string Token = "X-Sync-Token";
    public const string DeviceId = "X-Device-Id";
    public const string ServerId = "X-Pairnets-Server-Id";
    public const string Version = "X-Pairnets-Version";
    public const string Hash = "X-Pairnets-Hash";
    public const string ModifiedMs = "X-Pairnets-Modified-Ms";

    /// <summary>The app's version and system ("1.0.38; Windows"), shown in the Devices tab.</summary>
    public const string Client = "X-Pairnets-Client";
}

/// <summary>Shared JSON settings: camelCase, as used on the wire.</summary>
public static class PairnetsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}
