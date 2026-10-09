using System.Text.RegularExpressions;

namespace Pairnets.Core.Client;

/// <summary>
/// Pairnets Cloud, version 2 ("one address for everyone", cloud/RELAY.md): a computer signed in with a Pairnets account
/// talks to its server through the service, at <c>https://sync.pairnets.app/n/&lt;nest id&gt;/</c>. That address is
/// "relay mode". Every API path (<c>api/…</c>, <c>hub</c>) is relative to it, so the <c>/n/&lt;nest id&gt;/</c> part
/// carries through on its own. These helpers tell the apps whether they are in relay mode and what that changes.
/// </summary>
public static partial class Relay
{
    /// <summary>The Pairnets service: where computers sign in with an account, and the start of every relay address.</summary>
    public static Uri DefaultServiceUrl { get; } = new("https://sync.pairnets.app/");

    /// <summary>
    /// The nest id in a relay address of <paramref name="service"/> (by default <see cref="DefaultServiceUrl"/>), or
    /// null when <paramref name="serverUrl"/> is not one: another host, another path, or not a nest id. The trailing
    /// slash may be missing; nothing may follow the id.
    /// </summary>
    public static string? NestId(string? serverUrl, Uri? service = null) =>
        Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var url) ? NestId(url, service) : null;

    /// <inheritdoc cref="NestId(string?, Uri?)"/>
    public static string? NestId(Uri? serverUrl, Uri? service = null)
    {
        if (serverUrl is null || !serverUrl.IsAbsoluteUri || !SameOrigin(serverUrl, service ?? DefaultServiceUrl)
            || serverUrl.Query.Length > 0 || serverUrl.Fragment.Length > 0 || serverUrl.UserInfo.Length > 0)
            return null;
        var match = RelayPath().Match(serverUrl.AbsolutePath);
        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>True when <paramref name="serverUrl"/> is a relay address: the computer reaches its server through the service.</summary>
    public static bool IsRelayAddress(string? serverUrl, Uri? service = null) => NestId(serverUrl, service) is not null;

    /// <inheritdoc cref="IsRelayAddress(string?, Uri?)"/>
    public static bool IsRelayAddress(Uri? serverUrl, Uri? service = null) => NestId(serverUrl, service) is not null;

    /// <summary>True for a nest id the service could have made (<c>nst_</c> and 26 base32 characters, CONTRACT §1.2).</summary>
    public static bool IsNestId(string? id) => id is not null && NestIdPattern().IsMatch(id);

    /// <summary>The relay address of a nest: <c>https://sync.pairnets.app/n/&lt;nest id&gt;/</c>.</summary>
    public static Uri AddressOf(string nestId, Uri? service = null)
    {
        if (!IsNestId(nestId))
            throw new ArgumentException("Not a nest id.", nameof(nestId));
        return new Uri(Origin(service) + "/n/" + nestId + "/");
    }

    /// <summary>The service's address without a trailing slash ("https://sync.pairnets.app").</summary>
    public static string Origin(Uri? service = null) => (service ?? DefaultServiceUrl).GetLeftPart(UriPartial.Authority);

    /// <summary>The account page on the service: the servers, and the computers that use each, with Remove.</summary>
    public static string AccountUrl(Uri? service = null) => Origin(service) + "/account";

    /// <summary>
    /// Where "Manage computers" goes: the account page in relay mode (a relayed nest has no website of its own), the
    /// nest's own Devices page otherwise, or null when there is neither (a nest without its own name yet).
    /// </summary>
    public static string? ManageComputersUrl(string? serverUrl, string? nestUrl, Uri? service = null) =>
        IsRelayAddress(serverUrl, service) ? AccountUrl(service)
        : string.IsNullOrEmpty(nestUrl) ? null
        : nestUrl.TrimEnd('/') + "/devices";

    /// <summary>
    /// The "Add another computer" steps. In relay mode the new computer signs in with the same Pairnets account
    /// (<paramref name="accountEmail"/> when known); otherwise it types this nest's name (from <paramref name="nestUrl"/>).
    /// </summary>
    public static string AddComputerSteps(string? serverUrl, string? accountEmail, string? nestUrl, Uri? service = null)
    {
        if (IsRelayAddress(serverUrl, service))
        {
            var account = string.IsNullOrWhiteSpace(accountEmail) ? "the same account" : $"the same account ({accountEmail.Trim()})";
            return "On the computer you want to add:\n\n" +
                "1. Install Pairnets (pairnets.app/add).\n" +
                $"2. Open Pairnets and choose Continue with email (or Google) with {account}.\n\n" +
                "Then allow it in the browser and pick this server. It syncs the same files as this one.";
        }
        return "On the computer you want to add:\n\n" +
            "1. Install Pairnets (pairnets.app/add).\n" +
            $"2. Open Pairnets, type {(Uri.TryCreate(nestUrl, UriKind.Absolute, out var u) ? u.Authority : "your nest's name")} and press \"Sign in with your browser\".\n\n" +
            "Its request then pops up here and on your nest, where you allow it.";
    }

    /// <summary>True when <paramref name="url"/> is on the service itself (its own pages, or a relay address).</summary>
    public static bool IsServiceAddress(Uri? url, Uri? service = null) => url is not null && SameOrigin(url, service ?? DefaultServiceUrl);

    private static bool SameOrigin(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    [GeneratedRegex("^/n/(?<id>nst_[0-9a-hjkmnp-tv-z]{26})/?$")]
    private static partial Regex RelayPath();

    [GeneratedRegex("^nst_[0-9a-hjkmnp-tv-z]{26}$")]
    private static partial Regex NestIdPattern();
}
