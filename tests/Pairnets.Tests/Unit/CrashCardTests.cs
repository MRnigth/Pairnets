using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Crash cards: cleaned, one per kind of crash, kept on this computer, never sent.</summary>
public class CrashCardTests
{
    [Fact]
    public void ACardHasTheErrorAndTheCodeButNothingPersonal()
    {
        var error = OpenError(@"D:\Home\alex\Pairnets\Taxes 2026.pdf");

        var card = CrashCard.From(error, CrashPlace.Screen, "Windows app", ["alex"]);

        Assert.Equal("System.IO.FileNotFoundException", card.Error);
        Assert.Equal("Could not open '<text>'", card.Message);
        Assert.Contains("at Pairnets.Tests.Unit.CrashCardTests.FailToOpen(String path) in CrashCardTests.cs:line", card.Details);
        Assert.Equal(PairnetsInfo.ProductVersion, card.Version);
        Assert.Equal(12, card.Id.Length);
        Assert.False(card.Closed);
        var text = card.ToText();
        Assert.DoesNotContain("alex", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Taxes", text);
        Assert.DoesNotContain(SourceFolder(), text);
    }

    [Fact]
    public void TheSameCrashGetsTheSameIdWhateverTheFileOrMessage()
    {
        var a = CrashCard.From(OpenError("/home/a/one.txt"), CrashPlace.Screen, "Linux app");
        var b = CrashCard.From(OpenError("/home/b/two.txt"), CrashPlace.Screen, "Linux app");
        var other = CrashCard.From(Thrown(() => throw new InvalidOperationException("x")), CrashPlace.Screen, "Linux app");

        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Id, other.Id);
    }

    [Fact]
    public void TheSameCrashAgainIsCountedOnOneCard()
    {
        using var dir = new TempDir("crash-cards");
        var first = CrashCard.From(OpenError("a"), CrashPlace.Task, "Mac app", now: DateTimeOffset.UtcNow.AddHours(-1));

        CrashCards.Add(dir.Path, first);
        CrashCards.Add(dir.Path, CrashCard.From(OpenError("b"), CrashPlace.Background, "Mac app"));

        var card = Assert.Single(CrashCards.All(dir.Path));
        Assert.Equal(2, card.Count);
        Assert.Equal(first.FirstSeen, card.FirstSeen);
        Assert.True(card.LastSeen > card.FirstSeen);
        Assert.True(card.Closed);
    }

    [Fact]
    public void OnlyTheNewestCardsAreKept()
    {
        using var dir = new TempDir("crash-cards");
        for (var i = 0; i < CrashCards.Keep + 5; i++)
        {
            var card = CrashCard.From(new InvalidOperationException("x"), CrashPlace.Screen, "Windows app", now: DateTimeOffset.UtcNow.AddMinutes(i)) with { Id = $"card{i:00}" };
            CrashCards.Add(dir.Path, card);
        }

        Assert.Equal(CrashCards.Keep, Directory.GetFiles(dir.Path, "crash-*.json").Length);
        Assert.False(File.Exists(Path.Combine(dir.Path, "crash-card00.json")));
    }

    [Fact]
    public void AClosedAppIsNoticedOnceAndAgainWhenItClosesAgain()
    {
        using var dir = new TempDir("crash-cards");
        CrashCards.Add(dir.Path, CrashCard.From(OpenError("a"), CrashPlace.Screen, "Windows app"));
        Assert.Empty(CrashCards.TakeNewCloses(dir.Path)); // it kept running: nothing to say at the next start

        CrashCards.Add(dir.Path, CrashCard.From(OpenError("a"), CrashPlace.Background, "Windows app"));
        Assert.Single(CrashCards.TakeNewCloses(dir.Path));
        Assert.Empty(CrashCards.TakeNewCloses(dir.Path));

        CrashCards.Add(dir.Path, CrashCard.From(OpenError("a"), CrashPlace.Background, "Windows app"));
        Assert.Single(CrashCards.TakeNewCloses(dir.Path));
    }

    [Fact]
    public void AnUnreadableCardIsReplacedNotAnError()
    {
        using var dir = new TempDir("crash-cards");
        var card = CrashCard.From(new InvalidOperationException("x"), CrashPlace.Screen, "Windows app");
        File.WriteAllText(Path.Combine(dir.Path, $"crash-{card.Id}.json"), "{ not json");

        Assert.Empty(CrashCards.All(dir.Path));
        CrashCards.Add(dir.Path, card);
        Assert.Equal(1, Assert.Single(CrashCards.All(dir.Path)).Count);
    }

    [Fact]
    public void TheCatcherKeepsACardForAnErrorInABackgroundTaskNobodyWaitedFor()
    {
        using var dir = new TempDir("crash-cards");
        using var catcher = new CrashCatcher(dir.Path, "Linux app", NullLogger.Instance, () => ["alex"]);
        catcher.Watch();

        var id = Guid.NewGuid().ToString("N")[..8];
        for (var attempt = 0; attempt < 20 && !CrashCards.All(dir.Path).Any(c => c.Message.Contains(id, StringComparison.Ordinal)); attempt++)
        {
            LeaveAFailedTask("lost in the background " + id);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(50);
        }

        var card = Assert.Single(CrashCards.All(dir.Path), c => c.Message.Contains(id, StringComparison.Ordinal));
        Assert.Equal(CrashPlace.Task, card.Place);
        Assert.False(card.Closed);
    }

    [Fact]
    public void TheCatcherStopsWatchingWhenDisposed()
    {
        using var dir = new TempDir("crash-cards");
        var catcher = new CrashCatcher(dir.Path, "Linux app", NullLogger.Instance, () => []);
        catcher.Watch();
        catcher.Dispose();

        LeaveAFailedTask("after dispose");
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Empty(CrashCards.All(dir.Path));
    }

    [Fact]
    public void AnErrorThatClosesTheAppIsKeptAsClosed()
    {
        using var dir = new TempDir("crash-cards");
        using var catcher = new CrashCatcher(dir.Path, "Windows app", NullLogger.Instance, () => []);

        catcher.Record(OpenError("a"), CrashPlace.Background);

        Assert.True(Assert.Single(CrashCards.All(dir.Path)).Closed);
    }

    [Fact]
    public async Task ReportABugShowsTheKeptCards()
    {
        using var dir = new TempDir("crash-cards");
        var card = CrashCard.From(OpenError("/home/me/Work/a.txt"), CrashPlace.Background, "Linux app");
        CrashCards.Add(dir.Path, card);

        var report = await BugReport.BuildAsync(new ClientSettings(), null, null, null, "Linux app", dir.Path);

        Assert.Contains("## Crash cards (cleaned, kept on this computer; 1)", report);
        Assert.Contains($"Crash {card.Id}: System.IO.FileNotFoundException (Pairnets had to close)", report);
        Assert.DoesNotContain("/home/me/Work", report);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailToOpen(string path) => throw new FileNotFoundException($"Could not open '{path}'", path);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeaveAFailedTask(string message)
    {
        var task = Task.Run(() => throw new InvalidOperationException(message));
        ((IAsyncResult)task).AsyncWaitHandle.WaitOne(); // finished, but nobody looks at its error
    }

    /// <summary>The same crash every time (one place in the code), whatever the path.</summary>
    private static Exception OpenError(string path) => Thrown(() => FailToOpen(path));

    private static string SourceFolder([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

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
}
