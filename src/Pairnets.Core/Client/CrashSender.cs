using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pairnets.Core.Client;

/// <summary>
/// "Send crash report to Pairnets": the crash cards the user has not sent yet, sent to the Pairnets service
/// (<c>POST /v1/crash</c> on sync.pairnets.app) only when they press Send, after seeing <see cref="Preview"/>.
/// Exactly the cards' own fields go out, plus the user's optional note: no token, account, device id, files or log.
/// </summary>
public static class CrashSender
{
    public const int MaxCards = 5;
    public const int MaxNote = 1000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The newest cards not sent since they last happened, at most <see cref="MaxCards"/>.</summary>
    public static IReadOnlyList<CrashCard> Unsent(string directory) =>
        CrashCards.All(directory).Where(c => c.SentAt is null).Take(MaxCards).ToList();

    /// <summary>What will be sent, as the user sees it before pressing Send.</summary>
    public static string Preview(IReadOnlyList<CrashCard> cards) =>
        string.Join(Environment.NewLine, cards.Select(c => c.ToText()));

    /// <summary>The request body: the cards' fields and the cleaned note, nothing else.</summary>
    public static string Body(IReadOnlyList<CrashCard> cards, string? note, IEnumerable<string?>? privateValues = null)
    {
        var values = privateValues?.ToList();
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : PrivacyCleaner.Clean(note.Trim(), values);
        if (cleanNote is { Length: > MaxNote })
            cleanNote = cleanNote[..MaxNote];
        var sent = cards.Take(MaxCards).Select(c => new SentCard(c.Id, c.App, c.Version, c.System, c.Place,
            PrivacyCleaner.HideSecrets(c.Error), PrivacyCleaner.HideSecrets(c.Message), PrivacyCleaner.HideSecrets(c.Details),
            c.Count, c.FirstSeen, c.LastSeen, c.Closed)).ToList();
        return JsonSerializer.Serialize(new SentReport(sent, cleanNote), Json);
    }

    /// <summary>
    /// Sends <paramref name="cards"/> and marks them sent in <paramref name="directory"/>. Returns null when it worked,
    /// or a short reason for the user. Never throws for network trouble.
    /// </summary>
    public static async Task<string?> SendAsync(string directory, IReadOnlyList<CrashCard> cards, string? note,
        IEnumerable<string?>? privateValues = null, Uri? service = null, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (cards.Count == 0)
            return null;
        using var http = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }, disposeHandler: handler is null)
        {
            BaseAddress = service ?? Relay.DefaultServiceUrl,
            Timeout = Timeout,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pairnets", PairnetsInfo.ProductVersion));
        try
        {
            using var content = new StringContent(Body(cards, note, privateValues), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("v1/crash", content, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 429)
                return "Too many crash reports were sent from here just now. Try again later.";
            if (!resp.IsSuccessStatusCode)
                return $"Pairnets couldn't take the report (error {(int)resp.StatusCode}). Try again later.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return "Couldn't reach Pairnets. Check the internet connection and try again.";
        }
        CrashCards.MarkSent(directory, cards.Select(c => c.Id), DateTimeOffset.UtcNow);
        return null;
    }

    private sealed record SentCard(string Id, string App, string Version, string System, CrashPlace Place, string Error,
        string Message, string Details, int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Closed);

    private sealed record SentReport(IReadOnlyList<SentCard> Cards, string? Note);
}
