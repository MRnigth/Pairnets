namespace Pairnets.Core;

/// <summary>
/// Pages on the public website (pairnets.app) that the apps link to: the sign-in screen's small print and
/// Settings → About Pairnets. Fixed addresses, unlike the account service, which a test or an own domain can change.
/// </summary>
public static class PairnetsLinks
{
    public const string Website = "https://pairnets.app/";
    public const string Help = Website + "help/";
    public const string Faq = Website + "faq/";
    public const string Contact = Website + "contact/";
    public const string Privacy = Website + "privacy/";
    public const string Terms = Website + "terms/";
    public const string Guidelines = Website + "guidelines/";
    public const string Cookies = Website + "cookies/";
    public const string Security = Website + "security/";
    public const string DeleteAccount = Website + "delete-account/";
    public const string Licenses = Website + "licenses/";

    /// <summary>"Made by MRnigth in Denmark": who runs Pairnets, as the website's footer and legal pages say.</summary>
    public const string MadeBy = "Made by MRnigth in Denmark";

    /// <summary>"Pairnets 1.0.58 · © 2026 Pairnets · MIT licence · Made by MRnigth in Denmark" (Settings → About Pairnets).</summary>
    public static string AboutLine =>
        $"{PairnetsInfo.ProductName} {PairnetsInfo.ProductVersion} · {PairnetsInfo.Copyright} · MIT licence · {MadeBy}";
}
