using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;

namespace Pairnets.Core.Api;

/// <summary>
/// The one place that turns an error into words for a person. Both apps, the sync log and the sign-in screens use it, so
/// every window says the same thing. It never returns a .NET type name, a raw HTTP status line, parser text or a file
/// path from the system's own message: each answer says what happened and what to do. The full details (type, message,
/// status) stay in the exception, which the callers write to the log and which the bug report collects.
/// </summary>
public static class FriendlyErrors
{
    public const string Offline = "Pairnets cannot reach the server. Check that this computer is online and that the server is on. Pairnets keeps trying.";
    public const string TimedOut = "The server took too long to answer. Pairnets tries again by itself in a moment.";
    public const string ServerProblem = "The server had a problem and could not answer. Pairnets tries again by itself in a moment.";
    public const string BadAnswer = "The server sent an answer Pairnets could not read. Update the server and the app to the same release, then try again.";
    public const string NotAllowed = "The server did not accept this computer's key. Sign in again from the Pairnets window.";
    public const string SlowDown = "The server asked Pairnets to slow down. It goes on by itself in a moment.";
    public const string DiskFull = "There is no space left on the disk. Free some space, then try again.";
    public const string FileLocked = "A file is open in another program. Close it, then try again.";
    public const string NoAccess = "Pairnets is not allowed to use that folder or file. Check its permissions, then try again.";
    public const string FileProblem = "Pairnets could not read or write a file on this computer. Check that the folder is still there and not full or locked, then try again.";
    public const string Generic = "Something unexpected went wrong. Try again. If it keeps happening, choose \"Report a bug\" in the Pairnets menu.";

    /// <summary>
    /// Words for <paramref name="ex"/>. Exceptions of Pairnets itself carry sentences written for people and keep them;
    /// everything else (the framework's, the system's, a parser's) is mapped by what kind of trouble it is.
    /// </summary>
    public static string Describe(Exception ex)
    {
        switch (ex)
        {
            case PlainMessageException plain:
                return plain.Message;
            case PairnetsSlowDownException:
                return SlowDown;
            case PairnetsAuthException auth:
                return Usable(auth.Message) ? auth.Message : NotAllowed;
            case DownloadMismatchException:
                return Usable(ex.Message) ? ex.Message : "A file came down damaged, so Pairnets threw it away. It tries again by itself.";
            case PairnetsNetworkException net:
                return Usable(net.Message) ? net.Message : DescribeInner(net.InnerException) ?? Offline;
            case PairnetsProtocolException proto:
                return Usable(proto.Message) ? proto.Message : BadAnswer;
            case Pairnets.Core.Sync.ScanFailedException scan:
                return Usable(scan.Message) ? scan.Message : FileProblem;
            case LocalFileReadException local:
                return (local.InnerException is { } inner ? DescribeInner(inner) : null) ?? FileProblem;
            case AggregateException { InnerException: { } first }:
                return Describe(first);
        }
        return DescribeInner(ex) ?? Generic;
    }

    /// <summary>Mapping for exceptions that are not Pairnets's own; null when it is none of the known kinds.</summary>
    private static string? DescribeInner(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case PlainMessageException or PairnetsAuthException or PairnetsNetworkException or PairnetsProtocolException:
                    return Describe(e);
                case TimeoutException or TaskCanceledException or OperationCanceledException:
                    return TimedOut;
                case HttpRequestException or SocketException:
                    return Offline;
                case JsonException:
                    return BadAnswer;
                case UnauthorizedAccessException or SecurityException:
                    return NoAccess;
                case DirectoryNotFoundException or FileNotFoundException:
                    return "A folder or file Pairnets needs is not there any more. Check that the Pairnets folder (and its drive) is still connected.";
                case PathTooLongException:
                    return "A file path is too long for this computer. Shorten the folder or file names, then try again.";
                case IOException io:
                    return DescribeIo(io);
            }
        }
        return null;
    }

    private static string DescribeIo(IOException io)
    {
        var code = io.HResult & 0xFFFF; // the Win32 error inside the HRESULT
        if (code is 112 or 39 || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)
            || io.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
            return DiskFull;
        if (code is 32 or 33 || io.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            return FileLocked;
        return FileProblem;
    }

    /// <summary>
    /// True when a sentence reads like plumbing (a type name, an HTTP status line, parser text) rather than words for a
    /// person. Pairnets's own messages are checked too, as a last guard, so a lazy <c>"... " + ex.Message</c> in Core cannot leak.
    /// </summary>
    public static bool LooksTechnical(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;
        string[] marks =
        [
            "Exception", "Server error", "Invalid JSON", "Empty JSON", "Unexpected status", "Server answered", "Unexpected error",
            "JsonReaderException", "System.Net", "System.IO", "System.Text", "System.Security", "at line", "BytePositionInLine", "LineNumber", "Transport endpoint", "Object reference",
            "HttpRequestException", "Response status code", "SocketError", "(401)",
        ];
        foreach (var mark in marks)
            if (text.Contains(mark, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Everything about an error, for the log and the bug report: the type, the message, the technical details, the inner errors.</summary>
    public static string Technical(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var details = e switch { PairnetsNetworkException n => n.Details, PairnetsProtocolException p => p.Details, _ => null };
            parts.Add($"{e.GetType().Name}: {e.Message}" + (details is null ? string.Empty : $" [{details}]"));
        }
        return string.Join(" <- ", parts);
    }

    private static bool Usable(string? text) => !LooksTechnical(text);
}

/// <summary>
/// An error whose message is already written for people (it names the problem and what to do). <see cref="FriendlyErrors.Describe"/>
/// shows it as it is.
/// </summary>
public class PlainMessageException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
