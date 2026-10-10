using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace Pairnets.Tests.E2E;

/// <summary>
/// The API tour against each kind of test server. The server in the test process and the real server program always
/// run; the server installed on the Linux test box only when the pre-deploy runner asks for it
/// (PAIRNETS_E2E_TARGET=installed-linux). Every run must really have done all the tour claims to cover.
/// </summary>
public sealed class ApiTourTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TheTourPassesOnTheServerInsideTheTests()
    {
        await using var target = await InProcessTarget.StartAsync();
        await TourAsync(target);
    }

    [Fact]
    public async Task TheTourPassesOnTheRealServerProgram()
    {
        await using var target = await ProcessTarget.StartAsync();
        await TourAsync(target);
        target.Server.AssertNeverPrinted([target.Token]);
    }

    [Fact]
    public async Task TheTourPassesOnTheInstalledLinuxServer()
    {
        if (!E2EEnvironment.WantsInstalledLinux)
            return; // only when the pre-deploy runner has set up the Linux test box
        await using var target = InstalledLinuxTarget.FromEnvironment();
        var (record, steps, elapsed) = await TourAsync(target);
        if (target.ReportPath is { } path)
            await WriteReportAsync(path, target, record, steps, elapsed);
    }

    private async Task<(TourRecord Record, IReadOnlyList<string> Steps, TimeSpan Elapsed)> TourAsync(IServerTarget target)
    {
        await using var tour = new ApiTour(target, output.WriteLine);
        var watch = Stopwatch.StartNew();
        try
        {
            await tour.RunAsync();
        }
        catch
        {
            output.WriteLine($"The tour stopped at: {tour.Steps.LastOrDefault()}");
            try
            {
                output.WriteLine("Last lines of the server's log:");
                foreach (var line in (await target.ServerLogAsync()).TakeLast(60))
                    output.WriteLine("  " + line);
            }
            catch (Exception ex)
            {
                output.WriteLine("(the server's log could not be read: " + ex.Message + ")");
            }
            throw;
        }
        watch.Stop();
        var serverSide = await target.CollectServerSideAsync(tour.Record);
        output.WriteLine($"[{target.Name}] tour finished in {watch.Elapsed.TotalSeconds:0.0} s: {tour.Record.ClientAnswered.Count} routes, "
            + $"{tour.Record.HubEvents.Count} hub messages, {tour.Record.CliForms.Count} command lines.");
        foreach (var unknown in tour.Record.Unknown)
            output.WriteLine("  outside the tour's list: " + unknown);
        AssertEverythingClaimedWasDone(tour.Record, serverSide);
        return (tour.Record, tour.Steps, watch.Elapsed);
    }

    /// <summary>What <see cref="ApiTour"/> says it covers must have happened in this very run.</summary>
    private static void AssertEverythingClaimedWasDone(TourRecord record, bool serverSide)
    {
        var problems = new List<string>();
        foreach (var route in ApiTour.Routes.Where(r => !record.ClientAnswered.Contains(r)))
            problems.Add($"ApiTour lists {route}, but the walk never got a real answer from it (only errors, or no request at all).");
        if (serverSide)
        {
            foreach (var route in ApiTour.Routes.Where(r => !record.ServerAnswered.Contains(r)))
                problems.Add($"ApiTour lists {route}, but by the server's own account it never answered it.");
        }
        foreach (var method in ApiTour.HubMethods.Where(m => !record.HubCalls.Contains(m)))
            problems.Add($"ApiTour lists the push channel method {method}, but the walk never called it.");
        foreach (var evt in ApiTour.HubEvents.Where(e => !record.HubEvents.Contains(e)))
            problems.Add($"ApiTour lists the push message {evt}, but no push channel heard it during the walk.");
        foreach (var form in ApiTour.CliForms.Where(f => !record.CliForms.Contains(f)))
            problems.Add($"ApiTour lists the command \"pairnets-server {form}\", but the walk never ran it.");
        foreach (var word in ApiTour.CliWords.Where(w => !record.CliWords.Contains(w)))
            problems.Add($"ApiTour lists the command word \"{word}\", but the walk never ran a command starting with it.");
        foreach (var option in ApiTour.CliOptions.Where(o => !record.CliArgs.Contains(o)))
            problems.Add($"ApiTour lists the option {option}, but the walk never used it.");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>The runner's proof that the tour ran on the Linux test box (docs/PREDEPLOY.md, PAIRNETS_E2E_REPORT).</summary>
    private static async Task WriteReportAsync(string path, IServerTarget target, TourRecord record, IReadOnlyList<string> steps, TimeSpan elapsed)
    {
        var report = new
        {
            target = target.Name,
            passed = true,
            finishedUtc = DateTimeOffset.UtcNow,
            seconds = Math.Round(elapsed.TotalSeconds, 1),
            routes = record.ClientAnswered.Select(r => r.ToString()).Order(StringComparer.Ordinal),
            hubMethods = record.HubCalls.Order(StringComparer.Ordinal),
            hubEvents = record.HubEvents.Order(StringComparer.Ordinal),
            commands = record.CliForms.Order(StringComparer.Ordinal),
            steps,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
