namespace Pairnets.Core;

/// <summary>
/// One link the nest downloads by itself (1.0.61.8): the owner pastes it in an app, the nest fetches the file into its own
/// folders, and the owner's computers may be off meanwhile. <see cref="State"/> is queued, running, done or failed.
/// <see cref="Source"/> is only the website's name ("releases.ubuntu.com"): the full link stays on the nest, since links
/// often carry private tokens. <see cref="Name"/> is the file's name once known, <see cref="Folder"/> the nest folder it goes
/// in ("" = the top). <see cref="Path"/> is where the finished file is in the nest (only when done). <see cref="Total"/> is
/// null while the website hasn't said how big it is. <see cref="Error"/> is one of <see cref="NestDownloadErrors"/> on a
/// failed download, or on a queued one waiting to try again after a dropped connection. <see cref="Plain"/>: on an encrypted
/// nest, a finished download waits in the nest's holding spot without end-to-end encryption (outside the encrypted files)
/// until one of the owner's computers locks it and puts it in place like any file it adds; <see cref="Path"/> is null until then.
/// </summary>
public sealed record NestDownload(
    string Id,
    string Source,
    string Name,
    string Folder,
    string State,
    long Bytes,
    long? Total,
    double BytesPerSecond,
    DateTimeOffset Added,
    DateTimeOffset? Finished = null,
    string? Path = null,
    string? Error = null,
    bool Plain = false)
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    public bool IsBusy => State is Queued or Running;
}

/// <summary>
/// The answer of <c>GET /api/downloads</c>: the nest's downloads (newest first), how many it runs at once, and whether the
/// nest is encrypted (<see cref="Sealed"/>: it only holds files locked by the owner's computers, so a download waits
/// without end-to-end encryption until one of them locks it, see <see cref="NestDownload.Plain"/>).
/// </summary>
public sealed record NestDownloadList(IReadOnlyList<NestDownload> Downloads, int AtOnce, bool Sealed = false);

/// <summary>Body of <c>POST /api/downloads</c>: the link (http or https) and the nest folder the file goes in (null: "Downloads").</summary>
public sealed record NestDownloadRequest(string? Url, string? Folder = null);

/// <summary>The error codes of a nest download, and what the apps say for each.</summary>
public static class NestDownloadErrors
{
    /// <summary>Not an http:// or https:// link.</summary>
    public const string NotWeb = "not_web_link";

    /// <summary>The link points inside the nest's own network (itself, a home-network or cloud-internal address).</summary>
    public const string Blocked = "address_not_allowed";

    /// <summary>The website said the file isn't there (404/410).</summary>
    public const string NotFound = "not_found";

    /// <summary>The website answered with another error (401, 403, 500...).</summary>
    public const string Refused = "website_refused";

    /// <summary>The file doesn't fit: the nest keeps <see cref="NestDownloadRules.KeepFree"/> free on its disk.</summary>
    public const string NoSpace = "no_space";

    /// <summary>The connection broke and the tries ran out (or the website couldn't be reached).</summary>
    public const string Lost = "connection_lost";

    /// <summary>Too many redirects, or a redirect to something that isn't a web link.</summary>
    public const string Redirects = "too_many_redirects";

    /// <summary>The nest folder can't take the file (a file has that folder's name, or it isn't shared with this computer).</summary>
    public const string BadFolder = "bad_folder";

    /// <summary>The queue is full (<see cref="NestDownloadRules.MaxKept"/>).</summary>
    public const string Full = "queue_full";

    /// <summary>The app's own code: the nest is from before nest downloads.</summary>
    public const string UpdateNest = "update_nest";

    /// <summary>What the "Download a link" window says when the nest refused: "Only http:// and https:// links can be downloaded."</summary>
    public static string Sentence(string? code)
    {
        var text = Text(code);
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    /// <summary>"Stopped: the website said the file doesn't exist", in both apps.</summary>
    public static string Text(string? code) => code switch
    {
        NotWeb => "only http:// and https:// links can be downloaded",
        Blocked => "that link points inside your own network, which the nest doesn't download from",
        NotFound => "the website said the file doesn't exist",
        Refused => "the website refused to send the file",
        NoSpace => "there isn't enough free space on the nest",
        Redirects => "the link kept sending the nest somewhere else",
        BadFolder => "the nest can't put a file in that folder",
        Full => "the nest already has the most downloads it keeps",
        UpdateNest => "the nest is too old for this. Update the nest first",
        _ => "the connection to the website was lost",
    };
}

/// <summary>The limits of nest downloads, shared by the nest and the apps' words.</summary>
public static class NestDownloadRules
{
    /// <summary>The folder a download goes in when none was picked.</summary>
    public const string DefaultFolder = "Downloads";

    /// <summary>How many download at once; the others wait.</summary>
    public const int AtOnce = 2;

    /// <summary>How many downloads the nest keeps listed (waiting, running and finished together).</summary>
    public const int MaxKept = 20;

    /// <summary>The free space a download never takes: the nest stops before its disk has less than this.</summary>
    public const long KeepFree = 5L * 1024 * 1024 * 1024;

    /// <summary>Finished and stopped downloads leave the list (and a stopped one's half-done file the disk) after this.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>The link as a web address, or null when it isn't an http:// or https:// link (spaces around it are fine).</summary>
    public static Uri? WebLink(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri : null;

    /// <summary>What the "Download a link" window says about what was typed, or null when the nest can be asked.</summary>
    public static string? LinkProblem(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "Paste the link of the file first."
        : WebLink(text) is null ? "That isn't a web link. It should start with https:// or http://."
        : null;
}
