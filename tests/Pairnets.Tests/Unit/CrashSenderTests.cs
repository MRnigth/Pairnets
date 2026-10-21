using System.Net;
using System.Text.Json;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>"Send crash report to Pairnets": only the card's own fields and the note go out, and only when sent.</summary>
public class CrashSenderTests
{
    private static readonly Uri Service = new("https://crash.example.test/");

    [Fact]
    public void TheBodyHoldsOnlyTheCardsFieldsAndTheCleanedNote()
    {
        var card = Card(@"D:\Home\alex\Pairnets\Taxes 2026.pdf");

        var body = CrashSender.Body([card], @"I opened D:\Home\alex\Pairnets\Taxes 2026.pdf on alex-laptop", ["alex-laptop"]);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(["cards", "note"], root.EnumerateObject().Select(p => p.Name));
        var sent = Assert.Single(root.GetProperty("cards").EnumerateArray());
        Assert.Equal(
            ["id", "app", "version", "system", "place", "error", "message", "details", "count", "firstSeen", "lastSeen", "closed"],
            sent.EnumerateObject().Select(p => p.Name));
        Assert.Equal(card.Id, sent.GetProperty("id").GetString());
        Assert.Equal("Background", sent.GetProperty("place").GetString());
        Assert.DoesNotContain("alex", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Taxes", body);
    }

    [Fact]
    public void AnEmptyNoteIsLeftOutAndALongOneIsCut()
    {
        var card = Card("a");

        using var empty = JsonDocument.Parse(CrashSender.Body([card], "   "));
        using var longOne = JsonDocument.Parse(CrashSender.Body([card], new string('x', 5000)));

        Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("note").ValueKind);
        Assert.Equal(CrashSender.MaxNote, longOne.RootElement.GetProperty("note").GetString()!.Length);
    }

    [Fact]
    public async Task SendingPostsToTheServiceAndMarksTheCardsSent()
    {
        using var dir = new TempDir("crash-send");
        CrashCards.Add(dir.Path, Card("a"));
        var cards = CrashSender.Unsent(dir.Path);
        HttpRequestMessage? seen = null;
        string? sentBody = null;
        var handler = new StubHandler(req =>
        {
            seen = req;
            sentBody = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var problem = await CrashSender.SendAsync(dir.Path, cards, "note", service: Service, handler: handler);

        Assert.Null(problem);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal(new Uri(Service, "v1/crash"), seen.RequestUri);
        Assert.Null(seen.Headers.Authorization);
        Assert.Contains("\"note\":\"note\"", sentBody);
        Assert.Empty(CrashSender.Unsent(dir.Path));
        Assert.NotNull(Assert.Single(CrashCards.All(dir.Path)).SentAt);
    }

    [Fact]
    public async Task AFailedSendKeepsTheCardsUnsentAndSaysWhy()
    {
        using var dir = new TempDir("crash-send");
        CrashCards.Add(dir.Path, Card("a"));
        var cards = CrashSender.Unsent(dir.Path);

        var busy = await CrashSender.SendAsync(dir.Path, cards, null, service: Service,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var down = await CrashSender.SendAsync(dir.Path, cards, null, service: Service,
            handler: new StubHandler(_ => throw new HttpRequestException("no route")));

        Assert.Contains("Too many", busy);
        Assert.Contains("Couldn't reach Pairnets", down);
        Assert.Single(CrashSender.Unsent(dir.Path));
    }

    [Fact]
    public void ASentCrashThatHappensAgainIsOfferedAgain()
    {
        using var dir = new TempDir("crash-send");
        var card = Card("a");
        CrashCards.Add(dir.Path, card);
        CrashCards.MarkSent(dir.Path, [card.Id], DateTimeOffset.UtcNow);
        Assert.Empty(CrashSender.Unsent(dir.Path));

        CrashCards.Add(dir.Path, Card("b"));

        var again = Assert.Single(CrashSender.Unsent(dir.Path));
        Assert.Equal(2, again.Count);
        Assert.Null(again.SentAt);
    }

    [Fact]
    public void AtMostFiveCardsAreOffered()
    {
        using var dir = new TempDir("crash-send");
        for (var i = 0; i < 8; i++)
            CrashCards.Add(dir.Path, Card("a") with { Id = $"{i:x12}" });

        Assert.Equal(CrashSender.MaxCards, CrashSender.Unsent(dir.Path).Count);
    }

    private static CrashCard Card(string path) =>
        CrashCard.From(Thrown(() => throw new FileNotFoundException($"Could not open '{path}'", path)), CrashPlace.Background, "Windows app", ["alex"]);

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException("Nothing was thrown.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
