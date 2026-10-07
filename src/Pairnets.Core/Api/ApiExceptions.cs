namespace Pairnets.Core.Api;

/// <summary>The server rejected the token (HTTP 401). Retrying will not help until settings change.</summary>
public sealed class PairnetsAuthException(string message) : Exception(message);

/// <summary>The server could not be reached, timed out, stalled, or failed (5xx). The pass is aborted and retried later.</summary>
public sealed class PairnetsNetworkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The server answered something unexpected for one file. Only that file is skipped.</summary>
public sealed class PairnetsProtocolException(string message) : Exception(message);

/// <summary>Reading the local file failed during an upload (locked, vanished, I/O error).</summary>
public sealed class LocalFileReadException(string message, Exception inner) : IOException(message, inner);
