using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pairnets.Core.Client;

/// <summary>
/// "Send bug report to Pairnets": the "Report a bug" text (<see cref="BugReport.BuildAsync"/>), cleaned of everything
/// personal (<see cref="PrivacyCleaner.Clean"/>), sent to the Pairnets service (<c>POST /v1/bugreport</c> on
/// sync.pairnets.app) only when the user presses Send, after seeing exactly that text (<see cref="Prepare"/>).
/// Exactly that text goes out, plus the user's optional note and the app's name, version and system: no token, account
/// or device id.
/// </summary>
public static class BugReportSender
{
    /// <summary>The longest report sent, in characters (the Worker's MAX_REPORT is 64,000).</summary>
    public const int MaxReport = 60_000;
    public const int MaxNote = 1000;
    /// <summary>When a report is too long, this much of its start (the versions, settings and status) is kept.</summary>
    private const int KeepStart = 20_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What a file or folder name becomes.</summary>
    public const string HiddenName = "<file>";
    /// <summary>At most this many names are read from the nest folders (<see cref="NamesIn"/>).</summary>
    public const int MaxNames = 20_000;

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A word that looks like a file name ("beach.jpg", "report-q3.xlsx", "nest.example.com"), but not the names of
    // Pairnets's own code ("Pairnets.Engine:", "System.IO.IOException", "SyncEngine.cs:line 42") or its log files.
    private static readonly Regex FileWord = new(
        @"(?<![\p{L}\p{N}_.\-])(?!(?:Pairnets|System|Microsoft|Avalonia|Windows)\.|pairnets-\d+\.log\b|[\p{L}\p{N}_]+\.cs:line)[\p{L}\p{N}_\-+~]+(?:\.[\p{L}\p{N}_\-+~]+)*\.[A-Za-z][A-Za-z0-9]{0,7}(?![\p{L}\p{N}_.(])",
        RegexOptions.CultureInvariant);

    // A path with a name hidden inside it ("/home/<private>/Pairnets" became "<path><private><path>") reads as one path.
    private static readonly Regex JoinedHidden = new(@"(?:<path>|<private>|<file>)(?:[\\/]?(?:<path>|<private>|<file>))+", RegexOptions.CultureInvariant);

    // Names too common to hide everywhere they appear: they are words the report itself uses.
    private static readonly HashSet<string> ReportWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pairnets", "System", "Microsoft", "Windows", "Linux", "Error", "Errors", "Status", "Settings", "Server", "Log", "Logs", "none", "never",
    };

    /// <summary>
    /// The report as it will be sent and as the user sees it before pressing Send: cleaned of file, folder and computer
    /// names, web and email addresses, IP addresses and ids, and cut in the middle when longer than <see cref="MaxReport"/>
    /// (the newest log lines at the end are kept).
    /// </summary>
    /// <param name="privateValues">Names this computer is known by (<see cref="PrivacyCleaner.ThisComputer(Settings.ClientSettings)"/>).</param>
    /// <param name="names">The names of the files and folders in the nest folders (<see cref="NamesIn"/>): hidden
    /// wherever they appear, also when they hold spaces, which the shape of a name alone cannot tell.</param>
    public static string Prepare(string report, IEnumerable<string?>? privateValues = null, IEnumerable<string>? names = null)
    {
        var text = report.Replace("\r\n", "\n");
        foreach (var name in (names ?? []).Where(n => n.Trim().Length >= 3 && !ReportWords.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
        {
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                text = Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])", HiddenName, Options);
        }
        text = PrivacyCleaner.Clean(text, privateValues);
        text = FileWord.Replace(text, HiddenName);
        text = JoinedHidden.Replace(text, "<path>").Trim();
        if (text.Length <= MaxReport)
            return text;
        var start = text[..KeepStart];
        var end = text[^(MaxReport - KeepStart - 200)..];
        var left = text.Length - start.Length - end.Length;
        return start + string.Create(CultureInfo.InvariantCulture, $"\n\n(… {left:N0} characters were left out here to keep the report short …)\n\n") + end;
    }

    /// <summary>
    /// The names of the files and folders in <paramref name="folders"/> (the nest folders), at most <paramref name="max"/>,
    /// for <see cref="Prepare"/>. Folders that can't be read are skipped; links are not followed.
    /// </summary>
    public static IReadOnlyCollection<string> NamesIn(IEnumerable<string?> folders, int max = MaxNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", options))
                {
                    names.Add(Path.GetFileName(path));
                    if (names.Count >= max)
                        return names;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // what was read so far still helps
            }
        }
        return names;
    }

    /// <summary>The request body: the prepared report, the cleaned note, and which app, version and system sent it.</summary>
    public static string Body(string prepared, string? note, string app, IEnumerable<string?>? privateValues = null)
    {
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : PrivacyCleaner.Clean(note.Trim(), privateValues);
        if (cleanNote is { Length: > MaxNote })
            cleanNote = cleanNote[..MaxNote];
        var system = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}";
        return JsonSerializer.Serialize(new SentReport(Cut(app, 40), PairnetsInfo.ProductVersion, Cut(system, 200), prepared, cleanNote), Json);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>
    /// Sends <paramref name="prepared"/> (from <see cref="Prepare"/>) with the note. Returns null when it worked, or a
    /// short reason for the user. Never throws for network trouble.
    /// </summary>
    public static async Task<string?> SendAsync(string prepared, string? note, string app, IEnumerable<string?>? privateValues = null,
        Uri? service = null, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }, disposeHandler: handler is null)
        {
            BaseAddress = service ?? Relay.DefaultServiceUrl,
            Timeout = Timeout,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pairnets", PairnetsInfo.ProductVersion));
        try
        {
            using var content = new StringContent(Body(prepared, note, app, privateValues), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("v1/bugreport", content, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 429)
                return "Too many bug reports were sent from here just now. Try again later.";
            if (!resp.IsSuccessStatusCode)
                return $"Pairnets couldn't take the report (error {(int)resp.StatusCode}). Try again later.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return "Couldn't reach Pairnets. Check the internet connection and try again.";
        }
        return null;
    }

    private sealed record SentReport(string App, string Version, string System, string Report, string? Note);
}

/// <summary>The words of "Send bug report" in "Report a bug" and its send window, the same in both apps.</summary>
public static class SendReportText
{
    public const string BannerTitle = "Want us to look at it?";
    public const string BannerText = "Send this bug report to Pairnets. Your file, folder and computer names are taken out, and you see exactly what is sent first.";
    public const string Button = "Send bug report…";
    public const string SentTitle = "Thank you. The bug report was sent.";
    public const string SentText = "We'll look at it. Only the cleaned report and your note went out.";

    public const string Title = "Send bug report";
    public const string Heading = "Send this bug report to us?";
    public const string Explanation = "This is exactly what will be sent to Pairnets. Your file, folder and computer names, web addresses and emails were taken out.";
    public const string NoteLabel = "What went wrong? (optional)";
    public const string Promise = "Only this and your note are sent. No files, token, email or account. Your IP address is not stored.";

    /// <summary>The green chip at the top of "Report a bug": what was sent so far.</summary>
    public static string Chip(bool reportSent, int crashesSent) => (reportSent, crashesSent) switch
    {
        (false, 0) => "Nothing was sent anywhere",
        (true, 0) => "Bug report sent to Pairnets",
        (false, 1) => "Crash report sent to Pairnets",
        (false, _) => "Crash reports sent to Pairnets",
        (true, _) => "Bug and crash reports sent to Pairnets",
    };

    /// <summary>The crash banner's line once the crash report was sent.</summary>
    public static string CrashSent(bool reportSent) =>
        reportSent ? "The crash report went out too." : "Only the crash report went out. The bug report below was not sent.";
}
