using Tether.Core;

namespace Tether.Server.Storage;

public enum ChangeStatus
{
    Ok,
    Unchanged,
    Conflict,
    CaseCollision,
    InvalidName,
    BadRequest,
    NotFound,
}

/// <summary>Result of a write operation on the store.</summary>
public sealed record ChangeResult(ChangeStatus Status, ManifestEntry? Entry = null, string? Message = null)
{
    public bool Changed => Status == ChangeStatus.Ok;
}

/// <summary>An open file plus the manifest entry it corresponds to.</summary>
public sealed record OpenedFile(ManifestEntry Entry, FileStream Stream);
