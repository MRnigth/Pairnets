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
    /// <param name="s">The latest status.</param>
    /// <param name="device">This computer's name.</param>
    /// <param name="serverUrl">The server address in the settings (used when the nest has no website address).</param>
    public static AccountSummary Build(StatusSnapshot s, string? device, string? serverUrl)
    {
        var name = string.IsNullOrWhiteSpace(device) ? "This computer" : device.Trim();
        var host = Host(s.NestUrl) ?? Host(serverUrl);
        var email = s.Account?.Email is { Length: > 0 } e ? e.Trim() : null;
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

        return new AccountSummary(FirstLetter(title), title, subtitle, host, string.Join(" · ", facts), name, deviceDetail);
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
