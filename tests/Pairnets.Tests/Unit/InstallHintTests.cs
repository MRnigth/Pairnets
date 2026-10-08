using System.Text.RegularExpressions;
using Pairnets.Server.Cli;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Wherever Pairnets tells someone to run install.sh with an option, install.sh really has that option.</summary>
public sealed partial class InstallHintTests
{
    /// <summary>Folders whose text is checked. docs/ and README.md are not checked yet.</summary>
    private static readonly string[] Checked = ["src", "deploy", "site"];

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "deploy", "install.sh")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }
        throw new FileNotFoundException("deploy/install.sh not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The options install.sh parses: the patterns of the <c>case "$1" in … esac</c> in its argument loop.</summary>
    private static HashSet<string> InstallOptions()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot, "deploy", "install.sh")).ReplaceLineEndings("\n");
        var start = script.IndexOf("case \"$1\" in", StringComparison.Ordinal);
        Assert.True(start >= 0, "install.sh no longer parses its options with case \"$1\" in");
        var end = script.IndexOf("esac", start, StringComparison.Ordinal);
        var options = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in CasePattern().Matches(script[start..end]))
        {
            foreach (var option in m.Groups[1].Value.Split('|'))
            {
                if (option.StartsWith('-'))
                    options.Add(option);
            }
        }
        return options;
    }

    /// <summary>Every text file in the checked folders (no build output, nothing binary), with its path from the repo root.</summary>
    private static IEnumerable<(string Path, string[] Lines)> TextFiles()
    {
        foreach (var folder in Checked)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, folder), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "bin" or "obj" or "node_modules"))
                    continue;
                var bytes = File.ReadAllBytes(file);
                if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
                    continue;
                yield return (relative, System.Text.Encoding.UTF8.GetString(bytes).ReplaceLineEndings("\n").Split('\n'));
            }
        }
    }

    [Fact]
    public void InstallShHasOnlyTheOptionsTheAppsTalkAbout()
    {
        var options = InstallOptions();
        Assert.NotEmpty(options);
        var problems = new List<string>();
        foreach (var (path, lines) in TextFiles())
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("--domain", StringComparison.Ordinal))
                    problems.Add($"{path}:{i + 1}: install.sh has no --domain (it is --public-url https://...)");
                foreach (Match hint in InstallCommand().Matches(lines[i]))
                {
                    foreach (Match option in OptionName().Matches(hint.Groups[1].Value))
                    {
                        if (!options.Contains(option.Value))
                            problems.Add($"{path}:{i + 1}: install.sh has no {option.Value} (it knows {string.Join(", ", options.Order())})");
                    }
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TheCheckSeesTheOptionsInACommand()
    {
        var hint = InstallCommand().Match("run: sudo ./install.sh --public-url https://nest.example.com --bind 192.0.2.1 then wait");
        Assert.Equal(new[] { "--public-url", "--bind" }, OptionName().Matches(hint.Groups[1].Value).Select(m => m.Value));
        // Words after an option's value are prose, not options.
        hint = InstallCommand().Match("(install.sh --public-url sets it): take the --whatever header");
        Assert.Equal(new[] { "--public-url" }, OptionName().Matches(hint.Groups[1].Value).Select(m => m.Value));
    }

    [Fact]
    public async Task OwnerLinkWithoutAPublicNameSaysHowToGiveItOne()
    {
        using var dir = new TempDir("owner-link");
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            var code = await CliCommands.RunAsync(["owner-link", "--data-dir", dir.Path], output, error, _ => null);

            Assert.Equal(1, code);
            Assert.Empty(output.ToString());
            Assert.Contains("sudo ./install.sh --public-url https://", error.ToString());
            Assert.DoesNotContain("--domain", error.ToString());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>A case pattern line such as <c>    --bind) BIND=…</c> or <c>-h|--help) …</c>.</summary>
    [GeneratedRegex(@"^\s*([-\w|]+)\)", RegexOptions.Multiline)]
    private static partial Regex CasePattern();

    /// <summary>"install.sh" followed by options, each with at most one value.</summary>
    [GeneratedRegex(@"install\.sh((?:[ \t]+--?[A-Za-z][\w-]*(?:[ \t]+(?!-)[^\s'""`<>)]+)?)+)")]
    private static partial Regex InstallCommand();

    [GeneratedRegex(@"(?<![\w-])--?[A-Za-z][\w-]*")]
    private static partial Regex OptionName();
}
