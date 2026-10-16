namespace Pairnets.Core.Client;

/// <summary>
/// Who this computer belongs to on the nest (from <c>GET /api/me</c>): the owner's email, how they signed in
/// when they let this computer in, and when. Older servers send none of it.
/// </summary>
public sealed record AccountInfo(string? Email, string? Method, DateTimeOffset? Added)
{
    /// <summary>"Signed in with Google", or null when the server did not say.</summary>
    public string? MethodText => Method?.Trim().ToLowerInvariant() switch
    {
        "google" => "Signed in with Google",
        "email" => "Signed in with email",
        "passkey" => "Signed in with a passkey",
        "password" => "Signed in with a password",
        _ => null,
    };
}

/// <summary>The account button, its menu and the Account page, as text ready to show.</summary>
public sealed record AccountSummary(
    string Initial,
    string Title,
    string Subtitle,
    string? NestHost,
    string NestDetail,
    string DeviceName,
    string DeviceDetail)
{
    /// <summary>
    /// The account's email: from the nest, else the one this computer signed in with. Null for a computer that never
    /// signed in with an account on an older nest (the page then shows the computer name).
    /// </summary>
    public string? Email { get; init; }

    /// <summary>"Google", "Email" or "–": how the owner signed in, for the "Signed in with" tile.</summary>
    public string MethodName { get; init; } = "–";

    /// <summary>"29 Sep 2026", or "–": when this computer was added.</summary>
    public string AddedText { get; init; } = "–";
    /// <param name="s">The latest status.</param>
    /// <param name="device">This computer's name.</param>
    /// <param name="serverUrl">
    /// The server address in the settings (used when the nest has no website address); null falls back to the one in
    /// <paramref name="s"/>.
    /// </param>
    public static AccountSummary Build(StatusSnapshot s, string? device, string? serverUrl)
    {
        var name = string.IsNullOrWhiteSpace(device) ? "This computer" : device.Trim();
        // A nest reached through sync.pairnets.app has no website of its own: name it by the address this computer uses.
        var host = Host(s.NestUrl) ?? Host(serverUrl) ?? Host(s.ServerUrl);
        var email = s.Account?.Email is { Length: > 0 } e ? e.Trim()
            : s.AccountEmail is { Length: > 0 } signedIn ? signedIn.Trim()
            : null;
        var title = email ?? name;
        var subtitle = s.Account?.MethodText ?? (host is null ? "Not signed in to a nest" : "Signed in on " + host);

        var facts = new List<string>();
        if (s.Server?.ServerVersion is { Length: > 0 } version)
            facts.Add("Pairnets server " + version);
        if (s.Server?.DiskFreeBytes is { } free)
            facts.Add(Format.Bytes(free) + " free");
        if (s.ConnectionText is { } connection)
            facts.Add(connection);

        var added = s.Account?.Added
            ?? s.Devices?.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.FirstSeen;
        var deviceDetail = added is { } at
            ? "Added " + at.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + " · Shown in conflict file names"
            : "Shown in conflict file names";

        var method = s.Account?.Method?.Trim().ToLowerInvariant() switch
        {
            "google" => "Google",
            "email" => "Email",
            "passkey" => "Passkey",
            "password" => "Password",
            _ => "–",
        };
        return new AccountSummary(FirstLetter(title), title, subtitle, host, string.Join(" · ", facts), name, deviceDetail)
        {
            Email = email,
            MethodName = method,
            AddedText = added is { } addedAt ? addedAt.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "–",
        };
    }

    private static string FirstLetter(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                return char.ToUpperInvariant(c).ToString();
        }
        return "?";
    }

    private static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Authority : null;
}
