using System.Text.RegularExpressions;
using Pairnets.Server.Cli;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

/// <summary>
/// The server's command lines as its own help text (<see cref="CliCommands.Usage"/>) lists them. Each line becomes a
/// "form" such as "history list &lt;path&gt;"; an optional flag gives two forms ("rescan" and "rescan --dry-run"), and
/// the bare program (running the server) is "(run)".
/// </summary>
public static partial class CliUsage
{
    public const string RunForm = "(run)";

    private const string Program = "pairnets-server";

    /// <summary>Every form in the usage text.</summary>
    public static IReadOnlyList<string> Forms() => Lines().SelectMany(Variants).Select(Join).Distinct().ToList();

    /// <summary>The options under "Options:" ("--data-dir").</summary>
    public static IReadOnlyList<string> Options()
    {
        var after = CliCommands.Usage.ReplaceLineEndings("\n").Split("Options:", 2);
        return after.Length < 2 ? [] : OptionLine().Matches(after[1]).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>The first words <see cref="CliCommands.IsCliCommand"/> accepts, read from its source.</summary>
    public static IReadOnlyList<string> Words()
    {
        var source = RepoPaths.Read("src", "Pairnets.Server", "Cli", "CliCommands.cs");
        var body = IsCliCommandBody().Match(source);
        Assert.True(body.Success, "CliCommands.IsCliCommand is no longer written the way CliUsage reads it; update CliUsage.Words.");
        return Quoted().Matches(body.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>The form a command line (without --data-dir and its folder) belongs to, or null.</summary>
    public static string? FormOf(IReadOnlyList<string> args)
    {
        var given = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--data-dir")
                i++; // and its folder
            else
                given.Add(args[i]);
        }
        foreach (var variant in Lines().SelectMany(Variants))
        {
            if (variant.Count == given.Count && variant.Zip(given).All(p => p.First.StartsWith('<') || p.First == p.Second))
                return Join(variant);
        }
        return null;
    }

    /// <summary>The usage lines that show a command, as tokens: "history restore &lt;path&gt; &lt;id&gt;" → [history, restore, &lt;path&gt;, &lt;id&gt;].</summary>
    private static IEnumerable<List<string>> Lines()
    {
        foreach (var raw in CliCommands.Usage.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimStart();
            if (!line.StartsWith(Program, StringComparison.Ordinal))
                continue;
            var after = line[Program.Length..];
            // The command ends where the description starts (two spaces or more); the bare program has only a description.
            var command = after.Length == 0 || after.StartsWith("  ", StringComparison.Ordinal) ? string.Empty : Description().Split(after.Trim())[0];
            yield return Token().Matches(command).Select(m => m.Value).ToList();
        }
    }

    private static IEnumerable<List<string>> Variants(List<string> tokens)
    {
        var optional = tokens.FindIndex(t => t.StartsWith('['));
        if (optional < 0)
        {
            yield return tokens;
            yield break;
        }
        var without = tokens.Where((_, i) => i != optional).ToList();
        var with = tokens.Select((t, i) => i == optional ? t.Trim('[', ']') : t).ToList();
        foreach (var variant in Variants(without).Concat(Variants(with)))
            yield return variant;
    }

    private static string Join(List<string> tokens) => tokens.Count == 0 ? RunForm : string.Join(' ', tokens);

    [GeneratedRegex(@"<[^>]*>|\[[^\]]*\]|\S+")]
    private static partial Regex Token();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Description();

    [GeneratedRegex(@"^\s*(--[\w-]+)", RegexOptions.Multiline)]
    private static partial Regex OptionLine();

    [GeneratedRegex(@"IsCliCommand\(string\[\] args\)\s*=>([^;]*);")]
    private static partial Regex IsCliCommandBody();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex Quoted();
}
