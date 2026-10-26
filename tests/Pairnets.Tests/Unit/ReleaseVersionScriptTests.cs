using System.Diagnostics;
using System.Text.RegularExpressions;
using Pairnets.Core;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// The release workflow's numbers (docs/DECISIONS.md, "Beta channels"): scripts/release-version.sh (version, channel and
/// build of a run) and scripts/data-versions.sh (version.json's "data"). The scripts run under bash: on Linux, and on
/// Windows with Git Bash (as the pre-deploy check does); without one of them those tests do nothing.
/// </summary>
public class ReleaseVersionScriptTests
{
    // ------------------------------------------------------------------ release-version.sh

    [Fact]
    public void ABetaIsTheNextStableNumber_FromTheNewestTag()
    {
        if (Bash is null)
            return;
        using var repo = RepoWithTags("v1.0.9", "v1.0.60", "v1.0.61.7", "v1.0.61.10", "v1.0.61.9", "vtest", "v1.0.99.1.1", "public-beta", "latest");

        Assert.Equal(("1.0.61.11", "public-beta", "12"), Run(repo, "refs/heads/beta/public", "12"));
        Assert.Equal(("1.0.61.11", "developer-beta", "13"), Run(repo, "refs/heads/beta/developer", "13"));
    }

    [Fact]
    public void ABetaAfterAThreePartVersion_GetsAFourthPart()
    {
        if (Bash is null)
            return;
        using var repo = RepoWithTags("v1.0.59", "v1.0.60");

        Assert.Equal(("1.0.60.1", "public-beta", "5"), Run(repo, "refs/heads/beta/public", "5"));
    }

    [Fact]
    public void ATagIsStable_AndMainIsMain()
    {
        if (Bash is null)
            return;
        using var repo = RepoWithTags("v1.0.61.7");

        Assert.Equal(("1.0.61.8", "stable", "40"), Run(repo, "refs/tags/v1.0.61.8", "40"));
        Assert.Equal(("1.0.41", "main", "41"), Run(repo, "refs/heads/main", "41"));
    }

    [Fact]
    public void AnUnknownBetaBranch_NoTags_OrNoRunNumber_Fail()
    {
        if (Bash is null)
            return;
        using var repo = RepoWithTags("v1.0.61.7");
        using var empty = RepoWithTags();

        Assert.Contains("unknown beta branch beta/other", Fails(repo, "refs/heads/beta/other", "1"));
        Assert.Contains("no v* release tag", Fails(empty, "refs/heads/beta/public", "1"));
        Assert.Contains("GITHUB_RUN_NUMBER is not a number", Fails(repo, "refs/heads/main", ""));
        Assert.Contains("not a version", Fails(repo, "refs/tags/v1.0.61.8-rc", "1"));
    }

    // ------------------------------------------------------------------ data-versions.sh

    [Fact]
    public void DataVersions_PrintsThisBuildsNumbers()
    {
        if (Bash is null)
            return;
        var (code, output, error) = RunBash(RepoPaths.Root, [ScriptPath("data-versions.sh")], new Dictionary<string, string>());

        Assert.True(code == 0, error);
        var c = DataVersions.Current;
        Assert.Equal($"{{\"settings\":{c.Settings},\"state\":{c.State},\"sealed\":{c.Sealed}}}", output.Trim());
    }

    [Theory]
    [InlineData("Settings/ClientSettings.cs", "SettingsUpgrades")]
    [InlineData("State/StateDb.cs", "SchemaVersion")]
    [InlineData("Crypto/SealedFile.cs", "const byte Version")]
    public void DataVersions_FailsLoudly_WhenAConstantIsMissing(string file, string named)
    {
        if (Bash is null)
            return;
        using var root = new TempDir("data-versions");
        foreach (var f in new[] { "Settings/ClientSettings.cs", "State/StateDb.cs", "Crypto/SealedFile.cs" })
        {
            var to = root.Combine(["src", "Pairnets.Core", .. f.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            var text = RepoPaths.Read(["src", "Pairnets.Core", .. f.Split('/')]);
            if (f == file) // the constant renamed: the script must not guess
                text = text.Replace("const int Current =", "const int Newest =").Replace("const int SchemaVersion =", "const int Schema =")
                    .Replace("const byte Version =", "const byte FormatVersion =");
            File.WriteAllText(to, text);
        }

        var (code, _, error) = RunBash(root.Path, [ScriptPath("data-versions.sh"), root.Path.Replace('\\', '/')], new Dictionary<string, string>());

        Assert.NotEqual(0, code);
        Assert.Contains(named, error);
    }

    /// <summary>The same patterns as scripts/data-versions.sh, against the real constants (no bash needed).</summary>
    [Fact]
    public void TheDataVersionPatterns_FindTheRealConstants()
    {
        static int Find(string file, string pattern, string? after = null)
        {
            var text = RepoPaths.Read(["src", "Pairnets.Core", .. file.Split('/')]);
            if (after is not null)
            {
                var start = Regex.Match(text, after, RegexOptions.Multiline);
                Assert.True(start.Success, $"no \"{after}\" in {file}");
                text = text[(start.Index + start.Length)..];
            }
            var m = Regex.Match(text, pattern, RegexOptions.Multiline);
            Assert.True(m.Success, $"scripts/data-versions.sh would not find \"{pattern}\" in {file}");
            return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var script = RepoPaths.Read("scripts", "data-versions.sh");
        Assert.Contains("'(^|[[:space:]])const int Current = [0-9]+;' 'static class SettingsUpgrades([^A-Za-z0-9_]|$)'", script);
        Assert.Contains("'(^|[[:space:]])const int SchemaVersion = [0-9]+;'", script);
        Assert.Contains("'(^|[[:space:]])const byte Version = [0-9]+;'", script);

        var settings = Find("Settings/ClientSettings.cs", @"(?:^|\s)const int Current = ([0-9]+);", @"static class SettingsUpgrades(?:[^A-Za-z0-9_]|$)");
        var state = Find("State/StateDb.cs", @"(?:^|\s)const int SchemaVersion = ([0-9]+);");
        var sealedVersion = Find("Crypto/SealedFile.cs", @"(?:^|\s)const byte Version = ([0-9]+);");

        Assert.Equal(new DataVersions(settings, state, sealedVersion), DataVersions.Current);
        Assert.Equal(Pairnets.Core.Settings.SettingsUpgrades.Current, settings);
        Assert.Equal(Pairnets.Core.State.StateDb.SchemaVersion, state);
        Assert.Equal(Pairnets.Core.Crypto.SealedHeader.Version, sealedVersion);
    }

    // ------------------------------------------------------------------ the workflow

    [Fact]
    public void BetaPushes_ReplaceTheirOwnPreRelease_NeverTheLatestRelease()
    {
        var workflow = RepoPaths.Read(".github", "workflows", "release.yml");
        Assert.Contains("branches: [main, beta/public, beta/developer]", workflow);
        Assert.Contains("fetch-depth: 0", workflow);
        Assert.Contains("if: startsWith(github.ref, 'refs/heads/beta/') && github.event_name == 'push'", workflow);
        Assert.Contains("group: beta-${{ github.ref_name }}", workflow);
        var create = Assert.Single(Regex.Matches(workflow, @"gh release create ""\$CHANNEL"" [^\n]*")).Value;
        Assert.Contains("--prerelease", create);
        Assert.Contains("--latest=false", create);
        Assert.DoesNotMatch(new Regex(@"--latest(\s|\\|$)"), create);
        Assert.Contains("gh release delete \"$CHANNEL\" --repo \"$GITHUB_REPOSITORY\" --cleanup-tag --yes || true", workflow);
        Assert.Contains("public-beta) title=\"Public beta $VERSION (build $BUILD)\"", workflow);
        Assert.Contains("developer-beta) title=\"Developer beta $VERSION (build $BUILD)\"", workflow);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>bash: on Linux the one on the PATH, on Windows Git Bash (not WSL's bash.exe); null when there is none.</summary>
    private static readonly string? Bash = FindBash();

    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows())
            return "bash";
        var roots = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } local ? Path.Combine(local, "Programs") : null };
        return roots.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.Combine(r!, "Git", "bin", "bash.exe")).FirstOrDefault(File.Exists);
    }

    private static string ScriptPath(string name) => RepoPaths.Of("scripts", name).Replace('\\', '/');

    private static (int Code, string Output, string Error) RunBash(string cwd, IEnumerable<string> args, Dictionary<string, string> env)
    {
        var start = new ProcessStartInfo(Bash!) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd, UseShellExecute = false };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        foreach (var name in new[] { "GITHUB_REF", "GITHUB_REF_NAME", "GITHUB_RUN_NUMBER", "GITHUB_OUTPUT" })
            start.Environment.Remove(name);
        foreach (var (k, v) in env)
            start.Environment[k] = v;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "bash did not finish");
        return (process.ExitCode, output.Result, error.Result);
    }

    private static TempDir RepoWithTags(params string[] tags)
    {
        var dir = new TempDir("release-version");
        var script = "set -e; git init -q; git -c user.name=t -c user.email=t@example.com commit -q --allow-empty -m one; "
            + string.Concat(tags.Select(t => $"git tag '{t}'; "));
        var (code, _, error) = RunBash(dir.Path, ["-c", script], new Dictionary<string, string>());
        Assert.True(code == 0, error);
        return dir;
    }

    private static (int Code, string Output, string Error) RunScript(TempDir repo, string gitRef, string run) =>
        RunBash(repo.Path, [ScriptPath("release-version.sh")], new Dictionary<string, string>
        {
            ["GITHUB_REF"] = gitRef,
            ["GITHUB_RUN_NUMBER"] = run,
        });

    private static (string Version, string Channel, string Build) Run(TempDir repo, string gitRef, string run)
    {
        var (code, output, error) = RunScript(repo, gitRef, run);
        Assert.True(code == 0, error);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(new[] { "version", "channel", "build" }, lines.Keys);
        return (lines["version"], lines["channel"], lines["build"]);
    }

    private static string Fails(TempDir repo, string gitRef, string run)
    {
        var (code, output, error) = RunScript(repo, gitRef, run);
        Assert.NotEqual(0, code);
        Assert.Equal("", output);
        return error;
    }
}
