using System.Text.RegularExpressions;
using Pairnets.Server.Web;

namespace Pairnets.Tests.Unit;

/// <summary>What the nest's website says: the buttons it names must be the apps' real ones, and it must describe what the nest really does.</summary>
public sealed class NestWebsiteTextTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pairnets.sln")))
                return dir.FullName;
        }
        throw new FileNotFoundException("Pairnets.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A file of the website as the server serves it (built into the program).</summary>
    private static string Site(string name)
    {
        var assembly = typeof(WebUi).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(r => r.Replace('\\', '/') == "WebUi/" + name);
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot, .. parts]));

    private static readonly string[] Texts =
    [
        Site("assets/nest.js"), Site("pages/devices.html"), Site("pages/link.html"), Site("pages/setup.html"),
        Source("src", "Pairnets.Server", "Web", "WebEndpoints.cs"),
    ];

    [Fact]
    public void ItNamesTheButtonsTheAppsReallyHave()
    {
        // The apps have no plain "Sign in" button: it is "Sign in with your browser" (or Google / email), and "Get a new code".
        foreach (var text in Texts)
            Assert.DoesNotMatch(new Regex("[Pp]ress Sign in"), text);

        var quoted = Texts.SelectMany(t => Regex.Matches(t, @"\b(?:[Pp]ress|[Oo]r) “([^”]+)”").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.Contains("Sign in with your browser", quoted);
        Assert.Contains("Get a new code", quoted);
        Assert.Contains("Continue with Google", quoted);
        var wpf = Source("src", "Pairnets.Client", "Ui", "SignInView.xaml");
        var avalonia = Source("src", "Pairnets.Desktop", "Views", "SignInView.axaml");
        foreach (var label in quoted)
        {
            Assert.Contains($"\"{label}\"", wpf);
            Assert.Contains($"\"{label}\"", avalonia);
        }
    }

    [Fact]
    public void NoComputerSwitchesToItsOwnKeyByItself()
    {
        // Since the sign-in update a computer on the shared token stops until it is signed in and approved.
        var js = Site("assets/nest.js");
        Assert.DoesNotContain("by itself at its next start", js);
        Assert.DoesNotContain("switched to their own key", js);
        Assert.Contains("Sign in to your nest", js);
        Assert.Contains("/devices/forget", js);
        Assert.Contains("id=\"forget-dialog\"", Site("pages/devices.html"));
    }

    [Fact]
    public void TheSetupPageLinksToSecurityAndOnlyClaimsALinkWhenItUsedOne()
    {
        var setup = Site("pages/setup.html");
        Assert.Contains("<a href=\"/security\">Security</a>", setup);
        Assert.Matches(new Regex("<p [^>]*id=\"claimed\"[^>]*hidden[^>]*>[^<]*<span[^>]*>✓ Setup link accepted"), setup);
        Assert.Contains("Choose a new password", setup);
    }

    [Fact]
    public void ItAsksBeforeTakingAWayInAway()
    {
        var js = Site("assets/nest.js");
        Assert.Contains("confirm(\"Turn off the old shared token?", js);
        Assert.Contains("confirm(\"Remove your password?", js);
        Assert.Contains("confirm(`Remove the passkey", js);
    }
}
