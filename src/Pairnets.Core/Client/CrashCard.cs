using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pairnets.Core.Client;

/// <summary>Where an error was caught.</summary>
public enum CrashPlace
{
    /// <summary>On the screen side (a window or button); the app kept running.</summary>
    Screen,

    /// <summary>Behind the scenes; the app had to close.</summary>
    Background,

    /// <summary>In a background task nobody was waiting for; the app kept running.</summary>
    Task,
}

/// <summary>
/// One kind of crash, as kept on this computer: the error and the Pairnets code it went through, cleaned of
/// anything personal (<see cref="PrivacyCleaner"/>). No log lines, file or folder names, computer name, account
/// or device id. The same crash again adds to <see cref="Count"/> instead of making a new card.
/// It is sent only when the user presses "Send crash report to Pairnets" (<see cref="CrashSender"/>).
/// </summary>
public sealed record CrashCard
{
    public const int MaxMessage = 500;
    public const int MaxDetails = 8000;

    /// <summary>Same error kind through the same Pairnets code = same id (line numbers and versions left out).</summary>
    public required string Id { get; init; }
    public required string App { get; init; }
    public required string Version { get; init; }
    public required string System { get; init; }
    public required CrashPlace Place { get; init; }
    public required string Error { get; init; }
    public required string Message { get; init; }
    public required string Details { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public int Count { get; init; } = 1;

    /// <summary>The app had to close because of it.</summary>
    public bool Closed { get; init; }

    /// <summary>The "Pairnets closed after an unexpected error" notice was shown for the last close.</summary>
    public bool Noticed { get; init; }

    /// <summary>When the user sent it to Pairnets; null when not sent since it last happened.</summary>
    public DateTimeOffset? SentAt { get; init; }

    private static readonly Regex Frame = new(@"^\s*at\s+(?<method>[^(\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>A cleaned card for <paramref name="error"/>.</summary>
    /// <param name="privateValues">See <see cref="PrivacyCleaner.Clean"/>.</param>
    public static CrashCard From(Exception error, CrashPlace place, string app, IEnumerable<string?>? privateValues = null, DateTimeOffset? now = null)
    {
        var values = privateValues?.ToList();
        var when = now ?? DateTimeOffset.UtcNow;
        var details = PrivacyCleaner.Clean(error.ToString(), values);
        var type = error.GetType().FullName ?? error.GetType().Name;
        return new CrashCard
        {
            Id = IdFor(type, details),
            App = app,
            Version = PairnetsInfo.ProductVersion,
            System = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}",
            Place = place,
            Error = type,
            Message = Cut(PrivacyCleaner.Clean(error.Message, values), MaxMessage),
            Details = Cut(details, MaxDetails),
            FirstSeen = when,
            LastSeen = when,
            Closed = place == CrashPlace.Background,
        };
    }

    /// <summary>The card as plain text, as it appears in "Report a bug".</summary>
    public string ToText()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"Crash {Id}: {Error}{(Closed ? " (Pairnets had to close)" : string.Empty)}");
        sb.AppendLine(inv, $"Seen:    {Count} time{(Count == 1 ? string.Empty : "s")}, first {FirstSeen.ToLocalTime():yyyy-MM-dd HH:mm}, last {LastSeen.ToLocalTime():yyyy-MM-dd HH:mm}");
        sb.AppendLine(inv, $"Where:   {Place}, {App} {Version}");
        sb.AppendLine(inv, $"System:  {System}");
        sb.AppendLine(inv, $"Message: {Message}");
        sb.AppendLine(Details);
        return sb.ToString();
    }

    /// <summary>The error kind plus the first Pairnets places in its stack, hashed.</summary>
    private static string IdFor(string type, string details)
    {
        var frames = Frame.Matches(details).Select(m => m.Groups["method"].Value.Trim())
            .Where(m => m.StartsWith("Pairnets.", StringComparison.Ordinal)).Take(5);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(type + "\n" + string.Join("\n", frames)));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + " …(cut)";
}

/// <summary>The notice an app shows once when it starts after Pairnets had to close because of an error.</summary>
public static class CrashNotice
{
    public const string Title = "Pairnets closed unexpectedly";
    public const string Text = "Pairnets had to close after an unexpected error. A note about it, without your files or names, " +
        "is kept on this computer and shows in Report a bug, where you can send it to us. Nothing was sent.";
}

/// <summary>
/// The crash cards in one folder (%LocalAppData%\Pairnets\crashes), one file per kind of crash, newest
/// <see cref="Keep"/> kept. Never throws: keeping a card must not cause a second error.
/// </summary>
public static class CrashCards
{
    public const int Keep = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>Keeps <paramref name="card"/>, or adds it to the card already kept for the same crash.</summary>
    public static void Add(string directory, CrashCard card)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = PathFor(directory, card.Id);
                var kept = Read(path);
                var merged = kept is null ? card : card with
                {
                    FirstSeen = kept.FirstSeen,
                    Count = kept.Count + 1,
                    Closed = kept.Closed || card.Closed,
                    Noticed = card.Closed ? false : kept.Noticed,
                    SentAt = null, // it happened again: offer it again
                };
                Write(path, merged);
                Trim(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
    }

    /// <summary>All kept cards, newest first.</summary>
    public static IReadOnlyList<CrashCard> All(string directory)
    {
        lock (Gate)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return [];
                return Directory.EnumerateFiles(directory, "crash-*.json").Select(Read).OfType<CrashCard>()
                    .OrderByDescending(c => c.LastSeen).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>The crashes that closed the app since the last notice, now marked as noticed.</summary>
    public static IReadOnlyList<CrashCard> TakeNewCloses(string directory)
    {
        lock (Gate)
        {
            var fresh = All(directory).Where(c => c.Closed && !c.Noticed).ToList();
            foreach (var card in fresh)
            {
                try
                {
                    Write(PathFor(directory, card.Id), card with { Noticed = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return fresh;
        }
    }

    /// <summary>Marks the cards with these ids as sent (<see cref="CrashCard.SentAt"/>).</summary>
    public static void MarkSent(string directory, IEnumerable<string> ids, DateTimeOffset when)
    {
        lock (Gate)
        {
            foreach (var id in ids)
            {
                try
                {
                    var path = PathFor(directory, id);
                    if (Read(path) is { } card)
                        Write(path, card with { SentAt = when });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string PathFor(string directory, string id) => Path.Combine(directory, $"crash-{id}.json");

    private static CrashCard? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CrashCard>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // an unreadable card is replaced by the next one
        }
    }

    private static void Write(string path, CrashCard card)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(card, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static void Trim(string directory)
    {
        foreach (var old in Directory.EnumerateFiles(directory, "crash-*.json").Select(p => (Path: p, Card: Read(p)))
                     .OrderByDescending(f => f.Card?.LastSeen ?? DateTimeOffset.MinValue).Skip(Keep))
            File.Delete(old.Path);
    }
}
