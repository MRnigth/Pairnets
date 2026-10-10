using System.Text.RegularExpressions;

namespace Pairnets.Tests.Unit;

/// <summary>The public site in site/ (pairnets.app): every local link works, downloads point at real release files, the CSP holds.</summary>
public sealed class SiteTests
{
    private static readonly string Root = FindSite();

    private static string FindSite()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "site", "index.html")))
                return Path.Combine(dir.FullName, "site");
        }
        throw new FileNotFoundException("site/index.html not found above " + AppContext.BaseDirectory);
    }

    private static string RepoRoot => Path.GetDirectoryName(Root)!;

    private static IEnumerable<string> Pages() => Directory.EnumerateFiles(Root, "*.html", SearchOption.AllDirectories);

    private static Dictionary<string, string> Redirects() => File.ReadAllLines(Path.Combine(Root, "_redirects"))
        .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToDictionary(p => p[0], p => p[1]);

    [Fact]
    public void EveryLocalLinkImageAndScriptExists()
    {
        var redirects = Redirects();
        var missing = new List<string>();
        foreach (var page in Pages())
        {
            var html = File.ReadAllText(page);
            foreach (Match m in Regex.Matches(html, "(?:href|src)=\"(/[^\"#?]*)"))
            {
                var path = m.Groups[1].Value;
                if (path.StartsWith("//", StringComparison.Ordinal) || redirects.ContainsKey(path))
                    continue;
                var file = Path.Combine(Root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (path.EndsWith('/'))
                    file = Path.Combine(file, "index.html");
                if (!File.Exists(file))
                    missing.Add($"{Path.GetRelativePath(Root, page)} → {path}");
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void DownloadsPointAtFilesTheReleaseWorkflowBuilds()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "release.yml"));
        var downloads = Redirects().Where(r => r.Value.Contains("/releases/download/latest/", StringComparison.Ordinal)).ToList();
        Assert.True(downloads.Count >= 6);
        foreach (var (from, to) in downloads)
        {
            var file = to[(to.LastIndexOf('/') + 1)..];
            Assert.True(workflow.Contains(file, StringComparison.Ordinal), $"{from} → {file} is not built by release.yml");
        }
        Assert.True(Redirects().ContainsKey("/get.sh") && File.Exists(Path.Combine(RepoRoot, "deploy", "get.sh")));
    }

    [Fact]
    public void PagesObeyTheirOwnStrictContentSecurityPolicy()
    {
        var headers = File.ReadAllText(Path.Combine(Root, "_headers"));
        Assert.Contains("default-src 'none'", headers);
        Assert.Contains("script-src 'self'", headers);
        Assert.Contains("style-src 'self'", headers);
        Assert.Contains("frame-ancestors 'none'", headers);
        foreach (var page in Pages())
        {
            var html = File.ReadAllText(page);
            var name = Path.GetRelativePath(Root, page);
            Assert.False(Regex.IsMatch(html, @"<script(?![^>]*\bsrc=)"), $"{name} has an inline script");
            Assert.False(Regex.IsMatch(html, @"\sstyle="""), $"{name} has an inline style");
            Assert.False(Regex.IsMatch(html, @"<style[\s>]"), $"{name} has a style element");
            Assert.False(Regex.IsMatch(html, @"\son[a-z]+="""), $"{name} has an inline event handler");
        }
    }

    [Fact]
    public void EveryPageHasATitleADescriptionAndNoLeftoverPlaceholders()
    {
        foreach (var page in Pages())
        {
            var html = File.ReadAllText(page);
            Assert.Matches("<title>[^<]{5,}</title>", html);
            Assert.Matches("name=\"description\" content=\"[^\"]{10,}\"", html);
            Assert.DoesNotContain("TODO", html);
            Assert.DoesNotContain("Tether", html); // the product is called Pairnets now; no leftover old name
        }
    }

    [Fact]
    public void EveryPageLinksTheLegalPagesInItsFooter()
    {
        foreach (var page in Pages())
        {
            var html = File.ReadAllText(page);
            var name = Path.GetRelativePath(Root, page);
            var footer = Regex.Match(html, "<footer>.*?</footer>", RegexOptions.Singleline);
            Assert.True(footer.Success, $"{name} has no footer");
            foreach (var link in new[] { "/privacy/", "/terms/", "/guidelines/", "/cookies/", "/security/", "/faq/", "/contact/", "/delete-account/", "/licenses/" })
                Assert.True(footer.Value.Contains($"href=\"{link}\"", StringComparison.Ordinal), $"{name}'s footer does not link {link}");
        }
    }

    [Fact]
    public void ThePrivacyPolicyAndTheTermsSayWhoRunsPairnetsAndHowToReachThem()
    {
        foreach (var page in new[] { "privacy", "terms" })
        {
            var html = File.ReadAllText(Path.Combine(Root, page, "index.html"));
            Assert.Contains("MRnigth", html);
            Assert.Contains("Denmark", html);
            Assert.Contains("mailto:support@pairnets.app", html);
            Assert.Matches("Last updated \\d{1,2} [A-Z][a-z]+ 20\\d\\d", html);
        }
        var privacy = File.ReadAllText(Path.Combine(Root, "privacy", "index.html"));
        Assert.Contains("Datatilsynet", privacy); // where to complain, as the GDPR asks
        Assert.Contains("Limited Use requirements", privacy); // what Google asks of apps that use Google sign-in
    }

    [Fact]
    public void SecurityTxtHasAContactAndHasNotRunOut()
    {
        var lines = File.ReadAllLines(Path.Combine(Root, ".well-known", "security.txt"));
        Assert.Contains("Contact: mailto:support@pairnets.app", lines);
        Assert.Contains("Policy: https://pairnets.app/security/", lines);
        var expires = DateTimeOffset.Parse(lines.Single(l => l.StartsWith("Expires: ", StringComparison.Ordinal))["Expires: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(expires > DateTimeOffset.UtcNow, "security.txt has run out: move Expires on (at most a year ahead)");
    }

    [Fact]
    public void EveryPageHasTheSignInButtonThatShowsWhoIsSignedIn()
    {
        // site.js turns this button into "Account →" (and puts the username before it) when you are signed in.
        foreach (var page in Pages())
        {
            var header = Regex.Match(File.ReadAllText(page), "<header class=\"top\">.*?</header>", RegexOptions.Singleline);
            Assert.True(header.Success, $"{Path.GetRelativePath(Root, page)} has no top bar");
            Assert.Contains("<a class=\"button accent\" id=\"account-button\" href=\"https://nest.pairnets.app\">Sign in →</a>", header.Value);
        }
        var script = File.ReadAllText(Path.Combine(Root, "assets", "site.js"));
        Assert.Contains("fetch(\"https://sync.pairnets.app/v1/signed-in\", { credentials: \"include\"", script);
        Assert.DoesNotContain("innerHTML", script); // the username is only ever set as text
    }

    [Fact]
    public void ThePagesMayOnlyTalkToTheAccountService()
    {
        var csp = File.ReadAllLines(Path.Combine(Root, "_headers")).Single(l => l.Contains("Content-Security-Policy:", StringComparison.Ordinal));
        Assert.Contains("connect-src https://sync.pairnets.app;", csp);
        Assert.Single(Regex.Matches(csp, "connect-src"));
    }

    [Fact]
    public void TheSignInLinkGoesToTheNest()
    {
        Assert.Contains("href=\"https://nest.pairnets.app\"", File.ReadAllText(Path.Combine(Root, "index.html")));
        Assert.Equal("https://nest.pairnets.app", Redirects()["/nest"]);
    }
}
