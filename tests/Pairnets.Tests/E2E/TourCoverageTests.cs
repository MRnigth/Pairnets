using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

/// <summary>
/// Guards: whatever the server offers must be in the API tour. A new route, push-channel method or message, or
/// command line fails here until <see cref="ApiTour"/> covers it (and <see cref="ApiTourTests"/> then proves it does).
/// </summary>
public sealed partial class TourCoverageTests
{
    [Fact]
    public async Task EveryApiRequestTheServerAnswersIsInTheTour()
    {
        await using var server = await TestServer.StartAsync();
        var actual = Route.AllOf(server.Services.GetRequiredService<EndpointDataSource>());

        var problems = new List<string>();
        foreach (var route in actual.Where(r => !ApiTour.Routes.Contains(r)))
            problems.Add($"The server has an API request nobody tests: {route}. Add it to ApiTour.");
        foreach (var route in ApiTour.Routes.Where(r => !actual.Contains(r)))
            problems.Add($"ApiTour lists {route}, but the server has no such request any more. Take it out of ApiTour.");
        foreach (var twice in ApiTour.Routes.GroupBy(r => r).Where(g => g.Count() > 1))
            problems.Add($"ApiTour lists {twice.Key} more than once.");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void EveryPushChannelMethodTheAppsCanCallIsInTheTour()
    {
        var callable = typeof(SyncHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == typeof(SyncHub))
            .Select(m => m.Name)
            .Distinct()
            .ToList();

        var problems = new List<string>();
        foreach (var method in callable.Where(m => !ApiTour.HubMethods.Contains(m)))
            problems.Add($"The push channel (SyncHub) has a method the apps can call that nobody tests: {method}. Add it to ApiTour.");
        foreach (var method in ApiTour.HubMethods.Where(m => !callable.Contains(m)))
            problems.Add($"ApiTour lists the push channel method {method}, but SyncHub has no such method any more. Take it out of ApiTour.");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void EveryPushMessageTheServerSendsIsInTheTour()
    {
        var sent = new Dictionary<string, string>(StringComparer.Ordinal); // message name -> where it is sent from
        var problems = new List<string>();
        var serverDir = RepoPaths.Of("src", "Pairnets.Server");
        foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var where = Path.GetRelativePath(serverDir, file);
            foreach (Match call in HubSend().Matches(File.ReadAllText(file)))
            {
                if (MessageName(call.Groups["name"].Value) is { } name)
                    sent.TryAdd(name, where);
                else
                    problems.Add($"{where} sends a push message whose name this guard cannot read ({call.Groups["name"].Value}). Use a constant or a plain string.");
            }
        }
        Assert.NotEmpty(sent); // the scan itself still finds the server's messages

        foreach (var (name, where) in sent.Where(s => !ApiTour.HubEvents.Contains(s.Key)))
            problems.Add($"The server sends a push message nobody tests: {name} (from {where}). Add it to ApiTour.");
        foreach (var name in ApiTour.HubEvents.Where(e => !sent.ContainsKey(e)))
            problems.Add($"ApiTour lists the push message {name}, but the server never sends it any more. Take it out of ApiTour.");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void EveryCommandLineOfTheServerIsInTheTour()
    {
        var forms = CliUsage.Forms();
        var words = CliUsage.Words();
        var options = CliUsage.Options();
        Assert.NotEmpty(forms);
        Assert.NotEmpty(words);

        var problems = new List<string>();
        foreach (var form in forms.Where(f => !ApiTour.CliForms.Contains(f)))
            problems.Add($"The server's usage lists a command nobody tests: pairnets-server {form}. Add it to ApiTour.");
        foreach (var form in ApiTour.CliForms.Where(f => !forms.Contains(f)))
            problems.Add($"ApiTour lists the command \"pairnets-server {form}\", but the usage has no such line any more. Take it out of ApiTour.");
        foreach (var word in words.Where(w => !ApiTour.CliWords.Contains(w)))
            problems.Add($"pairnets-server takes \"{word}\" as a command (CliCommands.IsCliCommand), but nobody tests it. Add it to ApiTour.");
        foreach (var word in ApiTour.CliWords.Where(w => !words.Contains(w)))
            problems.Add($"ApiTour lists the command word \"{word}\", but pairnets-server no longer takes it. Take it out of ApiTour.");
        foreach (var option in options.Where(o => !ApiTour.CliOptions.Contains(o)))
            problems.Add($"The server's usage lists the option {option}, but nobody tests it. Add it to ApiTour.");
        foreach (var option in ApiTour.CliOptions.Where(o => !options.Contains(o)))
            problems.Add($"ApiTour lists the option {option}, but the usage has no such option any more. Take it out of ApiTour.");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>A push message's name from the first argument of a SendAsync: a string, or a constant somewhere in the server.</summary>
    private static string? MessageName(string argument)
    {
        if (argument.StartsWith('"'))
            return argument.Trim('"');
        var field = argument[(argument.LastIndexOf('.') + 1)..];
        var values = typeof(SyncHub).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name == field)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Distinct()
            .ToList();
        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>"Clients.All.SendAsync(Name", "Clients.Others.SendAsync(Name", "Clients.Client(id).SendAsync(Name", …</summary>
    [GeneratedRegex(@"Clients\.\w+(?:\([^)]*\))?\.Send(?:Core)?Async\(\s*(?<name>""[^""]*""|[A-Za-z_][\w.]*)")]
    private static partial Regex HubSend();
}
