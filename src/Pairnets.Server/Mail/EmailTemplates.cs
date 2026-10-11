using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Pairnets.Server.Mail;

/// <summary>One finished email: what the sender needs.</summary>
public sealed record EmailContent(string Subject, string Text, string Html);

/// <summary>
/// The nest's emails, filled in from the plain files in <c>Mail/Templates</c> (built into the program, like the
/// nest's website). Each email has an HTML and a text version; the HTML ones sit inside <c>layout.html</c>.
/// Blanks look like <c>{{name}}</c>. In HTML everything inserted is escaped, except blanks whose name ends in
/// "Html", which hold finished markup from these templates. A blank with no value is a bug and throws.
/// </summary>
public static class EmailTemplates
{
    public const string SignInSubject = "Your Pairnets sign-in link";
    public const string ConfirmSubject = "Confirm your email for Pairnets";
    public const string WelcomeSubject = "Welcome to Pairnets";

    private static readonly Regex Blank = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Files = new(LoadFiles);

    /// <summary>The link that signs in. <paramref name="nestUrl"/> is the nest's own address (the logo comes from it).</summary>
    public static EmailContent SignIn(string nestUrl, string host, string linkUrl) =>
        Build("signin", SignInSubject, "Sign in to your nest", "Open the link to sign in. It works once, for 15 minutes.", nestUrl, host,
            ("intro", $"Someone (hopefully you) asked to sign in to {host}."), ("url", linkUrl));

    /// <summary>The link that confirms an address before it may receive sign-in links.</summary>
    public static EmailContent Confirm(string nestUrl, string host, string linkUrl) =>
        Build("confirm", ConfirmSubject, "Confirm your email address", "Open the link to confirm this address. It works once, for 15 minutes.", nestUrl, host,
            ("intro", $"Someone (hopefully you) asked to use this address for sign-in links to {host}."), ("url", linkUrl));

    /// <summary>Sent once, right after an address was confirmed.</summary>
    public static EmailContent Welcome(string nestUrl, string host) =>
        Build("welcome", WelcomeSubject, "Welcome to Pairnets", "Your nest is ready. Here is how to connect a computer.", nestUrl, host,
            ("devicesUrl", nestUrl + "/devices"), ("securityUrl", nestUrl + "/security"));

    private static EmailContent Build(string name, string subject, string title, string preheader, string nestUrl, string host,
        params (string Key, string Value)[] own)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["title"] = title,
            ["preheader"] = preheader,
            ["logoUrl"] = nestUrl + "/assets/icon-256.png",
        };
        foreach (var (key, value) in own)
            values[key] = value;

        var text = Fill(Template(name + ".txt"), values, html: false);
        values["contentHtml"] = Fill(Template(name + ".html"), values, html: true);
        var html = Fill(Template("layout.html"), values, html: true);
        return new EmailContent(subject, text, html);
    }

    private static string Fill(string template, Dictionary<string, string> values, bool html) =>
        Blank.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
                throw new InvalidOperationException($"The email template has a blank {{{{{key}}}}} with no value.");
            return html && !key.EndsWith("Html", StringComparison.Ordinal) ? WebUtility.HtmlEncode(value) : value;
        });

    private static string Template(string file) =>
        Files.Value.TryGetValue(file, out var template) ? template : throw new InvalidOperationException($"The email template {file} is missing from the program.");

    private static Dictionary<string, string> LoadFiles()
    {
        const string prefix = "Mail/Templates/";
        var assembly = typeof(EmailTemplates).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files[name[prefix.Length..]] = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return files;
    }
}
