using System.Text.RegularExpressions;
using Pairnets.Server.Mail;

namespace Pairnets.Tests.Unit;

/// <summary>The nest's emails: nothing is left blank, nothing inserted can break the markup, nothing loads from elsewhere.</summary>
public sealed class EmailTemplatesTests
{
    private const string Nest = "https://nest.example.test";
    private const string Host = "nest.example.test";
    private const string Link = Nest + "/email-link#code=ABC-123&next=%2Flink%3Fcode%3DKQ7M-4PXD";

    public static TheoryData<string> AllEmails => new() { "signin", "confirm", "welcome" };

    private static EmailContent Make(string which, string host = Host, string link = Link) => which switch
    {
        "signin" => EmailTemplates.SignIn(Nest, host, link),
        "confirm" => EmailTemplates.Confirm(Nest, host, link),
        _ => EmailTemplates.Welcome(Nest, host),
    };

    [Theory]
    [MemberData(nameof(AllEmails))]
    public void EveryEmailIsFilledInCompletely(string which)
    {
        var mail = Make(which);

        Assert.DoesNotContain("{{", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", mail.Html, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(mail.Subject));
        Assert.Contains(Host, mail.Text, StringComparison.Ordinal);
        Assert.Contains(Host, mail.Html, StringComparison.Ordinal);
        Assert.StartsWith("<!doctype html>", mail.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("signin", EmailTemplates.SignInSubject)]
    [InlineData("confirm", EmailTemplates.ConfirmSubject)]
    [InlineData("welcome", EmailTemplates.WelcomeSubject)]
    public void SubjectsAreTheFixedOnes(string which, string subject) => Assert.Equal(subject, Make(which).Subject);

    [Theory]
    [InlineData("signin")]
    [InlineData("confirm")]
    public void LinkEmailsCarryTheLinkInBothVersionsAndSayHowLongItLasts(string which)
    {
        var mail = Make(which);

        Assert.Contains(Link, mail.Text, StringComparison.Ordinal); // the plain text has the link as it is
        Assert.Contains(Link.Replace("&", "&amp;", StringComparison.Ordinal), mail.Html, StringComparison.Ordinal); // the HTML has it escaped
        Assert.Contains("works once, for 15 minutes", mail.Text, StringComparison.Ordinal);
        Assert.Contains("works once, for 15 minutes", mail.Html, StringComparison.Ordinal);
        Assert.Contains("Ignore this email", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWelcomeHasNoSignInLinkButPointsToTheNestAndTheSite()
    {
        var mail = EmailTemplates.Welcome(Nest, Host);

        Assert.DoesNotContain("#code=", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("#code=", mail.Html, StringComparison.Ordinal);
        foreach (var expected in new[] { Nest + "/devices", Nest + "/security", "https://pairnets.app/add" })
        {
            Assert.Contains(expected, mail.Text, StringComparison.Ordinal);
            Assert.Contains(expected, mail.Html, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(AllEmails))]
    public void EverythingInsertedIsEscapedInHtmlAndLeftAloneInText(string which)
    {
        const string host = "<script>alert(1)</script>.example";
        var link = Nest + "/email-link#code=a\"onmouseover=\"x";

        var mail = Make(which, host, link);

        Assert.DoesNotContain("<script>", mail.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", mail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"onmouseover=\"", mail.Html, StringComparison.Ordinal); // a quote cannot end the link's attribute
        Assert.Contains(host, mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", mail.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllEmails))]
    public void ThePlainTextVersionIsPlainText(string which)
    {
        var text = Make(which).Text;

        Assert.DoesNotMatch(new Regex("</?[a-z][^>]*>", RegexOptions.IgnoreCase), text);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllEmails))]
    public void ThePictureComesFromTheNestAndNothingElseLoadsFromAnotherSite(string which)
    {
        var html = Make(which).Html;

        var sources = Regex.Matches(html, "src=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { Nest + "/assets/icon-256.png" }, sources);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", html, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllEmails))]
    public void TheDarkLookIsThere(string which)
    {
        var html = Make(which).Html;

        Assert.Contains("prefers-color-scheme: dark", html, StringComparison.Ordinal);
        Assert.Contains("color-scheme", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not a check: with PAIRNETS_EMAIL_PREVIEW_DIR set, writes each email as an HTML page (and its logo) there, to look
    /// at in a browser or turn into pictures. Without it the test does nothing.
    /// </summary>
    [Fact]
    public void WritesPreviewPagesWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("PAIRNETS_EMAIL_PREVIEW_DIR") is not { Length: > 0 } dir)
            return;
        Directory.CreateDirectory(dir);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "assets", "pairnets-icon-256.png")))
            root = root.Parent;
        Assert.NotNull(root);
        File.Copy(Path.Combine(root.FullName, "assets", "pairnets-icon-256.png"), Path.Combine(dir, "icon-256.png"), overwrite: true);

        const string nest = "https://home.example.com";
        const string link = nest + "/email-link#code=ZK3M7QXV9TCN4PBH2";
        foreach (var (name, mail) in new[]
                 {
                     ("signin", EmailTemplates.SignIn(nest, "home.example.com", link)),
                     ("confirm", EmailTemplates.Confirm(nest, "home.example.com", link)),
                     ("welcome", EmailTemplates.Welcome(nest, "home.example.com")),
                 })
        {
            File.WriteAllText(Path.Combine(dir, name + ".html"), mail.Html.Replace(nest + "/assets/icon-256.png", "icon-256.png", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(dir, name + ".txt"), mail.Text);
        }
    }
}
