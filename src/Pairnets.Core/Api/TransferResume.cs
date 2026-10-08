namespace Pairnets.Core.Api;

/// <summary>
/// Lets an upload in pieces outlive one attempt. The caller hands in the id of a session the server may
/// still have for exactly this content (or none) and is told the id of every session the upload opens, so it
/// can remember it (the engine keeps it in state.db) and continue from the server's count next time: after
/// a long network outage, Pause, or a restart of the app. <see cref="Id"/> is null once there is nothing
/// to continue: the upload finished, the server no longer has it, or it was given up.
/// </summary>
public sealed class UploadResume(string? id = null, Action<string>? started = null)
{
    /// <summary>The server's session that holds this upload so far, or null.</summary>
    public string? Id { get; private set; } = id;

    /// <summary>How many bytes the server already had when this attempt continued an earlier one (0 = started from the beginning).</summary>
    public long ResumedFrom { get; internal set; }

    /// <summary>A new session was opened (before any of its bytes were sent).</summary>
    internal void Started(string newId)
    {
        Id = newId;
        ResumedFrom = 0;
        started?.Invoke(newId);
    }

    /// <summary>There is no session to continue any more.</summary>
    internal void Forget() => Id = null;
}

/// <summary>
/// A download into a partial file that can be continued later. In: the version (<see cref="Hash"/>, the content
/// hash the server announced for it) whose first <see cref="Offset"/> bytes the destination already holds,
/// positioned right after them (0 = nothing yet). The destination must then be readable and seekable: those
/// bytes are read and hashed again, so the whole file is verified as before, and it is emptied when the server
/// sends the whole file instead because its version changed. Out: which version arrives.
/// </summary>
public sealed class DownloadResume(string? hash, long offset)
{
    public string? Hash { get; } = hash;

    public long Offset { get; } = offset;

    /// <summary>The version the server is sending (its hash header), as soon as its answer arrived.</summary>
    public string? ServedHash { get; set; }

    /// <summary>The whole size of that version, when the server said it.</summary>
    public long? ServedSize { get; set; }

    /// <summary>Where the bytes of this attempt start: <see cref="Offset"/> when the server continued, 0 when it sent the whole file.</summary>
    public long ContinuedFrom { get; set; }
}
