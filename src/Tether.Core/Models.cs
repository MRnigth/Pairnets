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

/// <summary>Answer of GET /api/info.</summary>
public sealed record ServerInfo(string ServerId, long Version, int ApiVersion);

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
