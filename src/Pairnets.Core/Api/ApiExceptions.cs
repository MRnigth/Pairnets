namespace Pairnets.Core.Api;

/// <summary>
/// The server rejected the token or key (HTTP 401). Retrying will not help until settings change.
/// <see cref="Code"/> tells why: <see cref="ErrorCodes.DeviceRemoved"/> (this computer was removed on the nest),
/// <see cref="ErrorCodes.SharedTokenOff"/> (the old shared token no longer works), <see cref="ServiceErrors.NestUnknown"/>
/// (the Pairnets service no longer knows the server this computer reached through it), or a plain wrong token.
/// </summary>
public sealed class PairnetsAuthException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;

    /// <summary>True when only signing in again helps (as opposed to a mistyped token).</summary>
    public bool NeedsSignIn => Code is ErrorCodes.DeviceRemoved or ErrorCodes.SharedTokenOff or ServiceErrors.NestUnknown;
}

/// <summary>
/// The server could not be reached, timed out, stalled, or failed (5xx). The pass is aborted and retried later.
/// <see cref="Code"/> is <see cref="ServiceErrors.NestOffline"/> when the Pairnets service said the server is not connected.
/// </summary>
public class PairnetsNetworkException(string message, Exception? inner = null, string? code = null, string? details = null) : Exception(message, inner)
{
    public string? Code { get; } = code;

    /// <summary>The technical side (status, original message) for the log and the bug report; never shown to people.</summary>
    public string? Details { get; } = details;
}

/// <summary>
/// The server, or the Pairnets service in front of it, answered 429: too many requests from this address for now.
/// Nothing is wrong with the file. Every request this client sends waits <see cref="RetryAfter"/> first (see
/// <c>PairnetsApiClient</c>), and the sync engine tries the same transfer again instead of counting it as failed.
/// Where nothing handles it, it counts as network trouble, so nothing half-sent is thrown away.
/// </summary>
public sealed class PairnetsSlowDownException(TimeSpan retryAfter)
    : PairnetsNetworkException($"The server asked to slow down; waiting {Math.Ceiling(retryAfter.TotalSeconds):0} s.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// A download arrived whole but its content does not match the server's hash. Unlike a cut connection, the
/// bytes received are worthless: a partial file it was continuing must not be continued again.
/// </summary>
public sealed class DownloadMismatchException(string message) : PairnetsNetworkException(message);

/// <summary>The server answered something unexpected for one file. Only that file is skipped.</summary>
public sealed class PairnetsProtocolException(string message, string? details = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The technical side (status, parser text) for the log and the bug report; never shown to people.</summary>
    public string? Details { get; } = details;
}

/// <summary>Reading the local file failed during an upload (locked, vanished, I/O error).</summary>
public sealed class LocalFileReadException(string message, Exception inner) : IOException(message, inner);
