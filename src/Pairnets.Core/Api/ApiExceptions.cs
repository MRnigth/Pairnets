namespace Pairnets.Core.Api;

/// <summary>
/// The server rejected the token or key (HTTP 401). Retrying will not help until settings change.
/// <see cref="Code"/> tells why: <see cref="ErrorCodes.DeviceRemoved"/> (this computer was removed on the nest),
/// <see cref="ErrorCodes.SharedTokenOff"/> (the old shared token no longer works), or a plain wrong token.
/// </summary>
public sealed class PairnetsAuthException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;

    /// <summary>True when only signing in again helps (as opposed to a mistyped token).</summary>
    public bool NeedsSignIn => Code is ErrorCodes.DeviceRemoved or ErrorCodes.SharedTokenOff;
}

/// <summary>The server could not be reached, timed out, stalled, or failed (5xx). The pass is aborted and retried later.</summary>
public sealed class PairnetsNetworkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server answered something unexpected for one file. Only that file is skipped.</summary>
public sealed class PairnetsProtocolException(string message) : Exception(message);

/// <summary>Reading the local file failed during an upload (locked, vanished, I/O error).</summary>
public sealed class LocalFileReadException(string message, Exception inner) : IOException(message, inner);
