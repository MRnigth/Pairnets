namespace Pairnets.Core.Api;

public enum ApiOutcome
{
    Ok,
    Conflict,
    CaseCollision,
    InvalidName,
    NotFound,
}

/// <summary>Outcome of an upload or delete.</summary>
public sealed record ApiResult(ApiOutcome Outcome, ManifestEntry? Entry, string? Message = null)
{
    public static ApiResult Ok(ManifestEntry entry) => new(ApiOutcome.Ok, entry);
}

/// <summary>Manifest entries plus the server identity and version they were read at.</summary>
public sealed record ManifestResponse(IReadOnlyList<ManifestEntry> Entries, string ServerId, long Version);

/// <summary>Outcome of a download. <see cref="Hash"/> is computed from the received bytes.</summary>
public sealed record DownloadResult(bool Found, string Hash, long Size, long ModifiedMs);

public enum ConnectionTestStatus
{
    Ok,
    InvalidUrl,
    Unreachable,
    BadToken,
    ServerError,
}

public sealed record ConnectionTestResult(ConnectionTestStatus Status, string Message, ServerInfo? Info = null);
