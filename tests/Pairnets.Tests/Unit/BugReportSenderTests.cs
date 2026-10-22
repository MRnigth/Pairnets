using System.Net;
using System.Text.Json;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>"Send bug report to Pairnets": the report is cleaned before it is shown, and exactly that goes out, only when sent.</summary>
public class BugReportSenderTests
{
    private static readonly Uri Service = new("https://report.example.test/");

    private static readonly string Report = string.Join(Environment.NewLine,
        "Pairnets bug report",
        "App:       Windows app 1.0.61",
        "## Settings (the token is never included)",
        "Server:              https://sync.pairnets.app/n/nst_0123456789abcdefghjkmnpqrs/",
        "This computer:       alex-laptop",
        @"Folder:              D:\Home\alex\Pairnets",
        "Account:             alex@example.com",
        "## Recent activity (newest first, up to 30)",
        "10:21:51 Uploaded   Taxes/2026 return.pdf",
        "## Log (last 300 lines of pairnets-20261022.log)",
        @"2026-10-22 10:23:38.451 [WRN] Pairnets.Engine: Evict failed for DCIM/100CAMERA/IMG_0001.JPG: Access to the path 'D:\Home\alex\Pairnets\DCIM\IMG_0001.JPG' is denied.",
        "2026-10-22 10:35:48.551 [INF] Pairnets.Tray: Connected to 192.0.2.44 as alex-laptop");

    [Fact]
    public void PrepareTakesOutFilesFoldersNamesAndAddresses()
    {
        var prepared = BugReportSender.Prepare(Report, ["alex-laptop"]);

        Assert.StartsWith("Pairnets bug report\nApp:       Windows app 1.0.61\n", prepared);
        Assert.DoesNotContain("\r", prepared);
        foreach (var gone in new[] { "alex", "Taxes", "IMG_0001", "DCIM", "nst_0123", "sync.pairnets.app", "192.0.2.44", "example.com" })
            Assert.DoesNotContain(gone, prepared, StringComparison.OrdinalIgnoreCase);
        // What finding the bug needs stays: the sections, the kind of message, and which part of Pairnets wrote it.
        Assert.Contains("## Log (last 300 lines of pairnets-20261022.log)", prepared);
        Assert.Contains("[WRN] Pairnets.Engine: Evict failed for <path>", prepared);
        Assert.Contains("This computer:       <private>", prepared);
        Assert.Contains("Folder:              <path>\n", prepared);
    }

    [Fact]
    public void APathWithANameHiddenInsideReadsAsOnePath()
    {
        var prepared = BugReportSender.Prepare("Folder: /home/alex/Pairnets\nSyncing /home/alex/Pairnets as alex-laptop", ["alex", "alex-laptop"]);

        Assert.Equal("Folder: <path>\nSyncing <path> as <private>", prepared);
    }

    [Fact]
    public void FileNamesAtTheTopOfTheNestAreHiddenToo()
    {
        var log = string.Join("\n",
            "2026-10-22 10:39:30.114 [INF] Pairnets.Engine: Downloaded Skærmbillede 2026-01-02 101010.png (71074 bytes)",
            "2026-10-22 10:39:30.205 [INF] Pairnets.Engine: Downloaded beach.jpg (561431 bytes)",
            "2026-10-22 10:39:30.300 [INF] Pairnets.Engine: Files in Holiday photos now live: NestOnly",
            "2026-10-22 10:39:31.000 [WRN] Pairnets.Engine: Download failed for gone.docx: System.IO.IOException: The disk is full",
            "   at Pairnets.Core.Sync.SyncEngine.DownloadAsync(String path) in SyncEngine.cs:line 42",
            "## Log (last 300 lines of pairnets-20261022.log)");

        var prepared = BugReportSender.Prepare(log, names: ["Skærmbillede 2026-01-02 101010.png", "Holiday photos", "Pairnets", "ab"]);

        Assert.Equal(string.Join("\n",
            "2026-10-22 10:39:30.114 [INF] Pairnets.Engine: Downloaded <file> (71074 bytes)",
            "2026-10-22 10:39:30.205 [INF] Pairnets.Engine: Downloaded <file> (561431 bytes)",
            "2026-10-22 10:39:30.300 [INF] Pairnets.Engine: Files in <file> now live: NestOnly",
            "2026-10-22 10:39:31.000 [WRN] Pairnets.Engine: Download failed for <file>: System.IO.IOException: The disk is full",
            "   at Pairnets.Core.Sync.SyncEngine.DownloadAsync(String path) in SyncEngine.cs:line 42",
            "## Log (last 300 lines of pairnets-20261022.log)"), prepared);
    }

    [Fact]
    public void NamesInListsTheNestFoldersFilesAndFolders()
    {
        using var dir = new TempDir("bug-names");
        Directory.CreateDirectory(Path.Combine(dir.Path, "Holiday photos", "Day 1"));
        File.WriteAllText(Path.Combine(dir.Path, "Holiday photos", "Day 1", "beach at noon.jpg"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "notes.txt"), "x");

        var names = BugReportSender.NamesIn([dir.Path, Path.Combine(dir.Path, "missing"), null]);

        Assert.Equal(["Day 1", "Holiday photos", "beach at noon.jpg", "notes.txt"], names.Order(StringComparer.Ordinal));
        Assert.Equal(2, BugReportSender.NamesIn([dir.Path], max: 2).Count);
    }

    [Fact]
    public void ALongReportIsCutInTheMiddleKeepingItsStartAndTheNewestLogLines()
    {
        var log = string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"2026-10-22 10:00:00.000 [INF] Pairnets.Engine: Pass {i} finished: up 0, down 0"));
        var prepared = BugReportSender.Prepare("Pairnets bug report\nApp: Windows app 1.0.61\n" + log);

        Assert.True(prepared.Length <= BugReportSender.MaxReport, $"{prepared.Length} characters");
        Assert.StartsWith("Pairnets bug report\nApp: Windows app 1.0.61\n", prepared);
        Assert.EndsWith("Pass 3999 finished: up 0, down 0", prepared);
        Assert.Contains("characters were left out here", prepared);
    }

    [Fact]
    public void TheBodyHoldsTheReportAsShownTheCleanedNoteAndWhichAppSentIt()
    {
        var prepared = BugReportSender.Prepare(Report, ["alex-laptop"]);

        var body = BugReportSender.Body(prepared, @"It broke after I moved D:\Home\alex\Pairnets on alex-laptop", "Windows app", ["alex-laptop"]);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(["app", "version", "system", "report", "note"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Windows app", root.GetProperty("app").GetString());
        Assert.Equal(PairnetsInfo.ProductVersion, root.GetProperty("version").GetString());
        Assert.Equal(prepared, root.GetProperty("report").GetString());
        Assert.Equal("It broke after I moved <path> on <private>", root.GetProperty("note").GetString());
        Assert.DoesNotContain("alex", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyNoteIsLeftOutAndALongOneIsCut()
    {
        using var empty = JsonDocument.Parse(BugReportSender.Body("report", "   ", "Mac app"));
        using var longOne = JsonDocument.Parse(BugReportSender.Body("report", new string('x', 5000), "Mac app"));

        Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("note").ValueKind);
        Assert.Equal(BugReportSender.MaxNote, longOne.RootElement.GetProperty("note").GetString()!.Length);
    }

    [Fact]
    public async Task SendingPostsToTheServiceWithoutSigningIn()
    {
        HttpRequestMessage? seen = null;
        string? sentBody = null;
        var handler = new StubHandler(req =>
        {
            seen = req;
            sentBody = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var problem = await BugReportSender.SendAsync("Pairnets bug report", "note", "Linux app", service: Service, handler: handler);

        Assert.Null(problem);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal(new Uri(Service, "v1/bugreport"), seen.RequestUri);
        Assert.Null(seen.Headers.Authorization);
        Assert.Equal("application/json", seen.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("\"report\":\"Pairnets bug report\"", sentBody);
        Assert.Contains("\"note\":\"note\"", sentBody);
    }

    [Fact]
    public async Task AFailedSendSaysWhy()
    {
        var busy = await BugReportSender.SendAsync("r", null, "Windows app", service: Service,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var refused = await BugReportSender.SendAsync("r", null, "Windows app", service: Service,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var down = await BugReportSender.SendAsync("r", null, "Windows app", service: Service,
            handler: new StubHandler(_ => throw new HttpRequestException("no route")));

        Assert.Contains("Too many bug reports", busy);
        Assert.Contains("error 400", refused);
        Assert.Contains("Couldn't reach Pairnets", down);
    }

    [Theory]
    [InlineData(false, 0, "Nothing was sent anywhere")]
    [InlineData(true, 0, "Bug report sent to Pairnets")]
    [InlineData(false, 1, "Crash report sent to Pairnets")]
    [InlineData(false, 3, "Crash reports sent to Pairnets")]
    [InlineData(true, 2, "Bug and crash reports sent to Pairnets")]
    public void TheChipSaysWhatWasSent(bool reportSent, int crashesSent, string chip) =>
        Assert.Equal(chip, SendReportText.Chip(reportSent, crashesSent));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(reply(request));
    }
}
