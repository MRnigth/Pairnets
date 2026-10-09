using System.Text.RegularExpressions;
using Pairnets.Server.Auth;

namespace Pairnets.Server;

/// <summary>Server configuration (section "Sync", environment variables Sync__X, or SYNC_TOKEN).</summary>
public sealed partial class SyncOptions
{
    public const int MinimumTokenLength = 16;

    /// <summary>Shared secret; required for serve mode, at least 16 characters.</summary>
    public string Token { get; set; } = string.Empty;

    public string DataDir { get; set; } = "/var/lib/pairnets";

    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>Always keep at least this many history versions per file, regardless of age.</summary>
    public int HistoryMinVersions { get; set; } = 5;

    /// <summary>Delay before the first history purge after startup.</summary>
    public TimeSpan PurgeInitialDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often live push connections are checked against the list of computers (Sync:ConnectionCheckInterval, "00:00:10").</summary>
    public TimeSpan ConnectionCheckInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An upload that delivers no data for this long is abandoned and its temp file deleted. Covers
    /// connections that die without a reset (laptop dropped off Wi-Fi). Matches the client's watchdog.
    /// </summary>
    public TimeSpan UploadStallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The root-run self-update script install.sh puts next to the binary (see ServerUpdater).</summary>
    public string UpdaterScript { get; set; } = Path.Combine(AppContext.BaseDirectory, "update.sh");

    /// <summary>
    /// Where update requests go and update.sh reports back. It must be the folder the root updater
    /// watches (/var/lib/pairnets/update, set by install.sh), even when DataDir is elsewhere.
    /// Unset: DataDir/update.
    /// </summary>
    public string? UpdateDir { get; set; }

    /// <summary>
    /// The server sits behind a tunnel or reverse proxy on this machine (install.sh --public-url
    /// sets it): take the client's address from CF-Connecting-IP / X-Forwarded-For on loopback
    /// connections. Leave it off when clients connect directly. In relay mode the client's address is always taken
    /// this way (the service's tunnel is on this machine), from X-Pairnets-Client-IP first.
    /// </summary>
    public bool TrustProxyHeaders { get; set; }

    /// <summary>Listen for HTTPS directly, e.g. "https://0.0.0.0:443". Unset behind the Cloudflare Tunnel (it terminates TLS).</summary>
    public string? HttpsUrl { get; set; }

    /// <summary>Folder with fullchain.pem and privkey.pem for the direct HTTPS listener. Unset: DataDir/tls.</summary>
    public string? TlsDir { get; set; }

    /// <summary>How often a loaded certificate is checked for a renewed one on disk.</summary>
    public TimeSpan TlsRecheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The nest's own address, e.g. "https://sync.pairnets.app" (Sync:PublicUrl, PUBLIC_URL or PAIRNETS_PUBLIC_URL). Used to build
    /// the sign-in website's own origin and to bind passkeys. Behind the Cloudflare Tunnel the server itself
    /// listens on loopback, so this is how it learns its public name.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// The passkey "relying party id" (Sync:PasskeyRpId): the domain passkeys are tied to. Default: the nest's own host name.
    /// Set it to a parent domain (pairnets.app) only if something else on that domain should share the passkeys.
    /// It must be the nest's host or a parent of it.
    /// </summary>
    public string? PasskeyRpId { get; set; }

    // ---- email sign-in links (optional): any SMTP service, e.g. Resend (smtp.resend.com, user "resend", the API key as password)

    /// <summary>SMTP server for sign-in emails (Sync:SmtpHost). Unset: email links are off.</summary>
    public string? SmtpHost { get; set; }

    /// <summary>587 (STARTTLS) by default.</summary>
    public int SmtpPort { get; set; } = 587;

    public string? SmtpUser { get; set; }

    public string? SmtpPassword { get; set; }

    /// <summary>The sender, e.g. "Pairnets nest &lt;nest@pairnets.app&gt;" (the domain must be allowed to send, SPF/DKIM).</summary>
    public string? SmtpFrom { get; set; }

    /// <summary>Upgrade the connection with STARTTLS (default). Only turn off for a mail server on this machine.</summary>
    public bool SmtpUseTls { get; set; } = true;

    /// <summary>Email links can be used: the nest has a name to link to and a way to send mail.</summary>
    public bool EmailConfigured => PublicUrl is not null && !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(SmtpFrom);

    // ---- Sign in with Google (optional): your own OAuth client (type "Web"), redirect {PublicUrl}/auth/google/callback

    public string? GoogleClientId { get; set; }

    public string? GoogleClientSecret { get; set; }

    public string GoogleAuthUrl { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    public string GoogleTokenUrl { get; set; } = "https://oauth2.googleapis.com/token";

    public bool GoogleConfigured => PublicUrl is not null && !string.IsNullOrWhiteSpace(GoogleClientId) && !string.IsNullOrWhiteSpace(GoogleClientSecret);

    // ---- relay mode (Pairnets Cloud, cloud/RELAY.md §3): the apps reach the nest through https://sync.pairnets.app.
    //      install.sh writes these when it links the server to a Pairnets account.

    public const string DefaultRelayServiceUrl = "https://sync.pairnets.app";

    /// <summary>
    /// This nest's id at the Pairnets service, "nst_" and 26 characters (Sync:RelayNestId). Together with
    /// <see cref="RelayKey"/> it turns on relay mode: the apps reach the nest through the service, which signs its own
    /// calls to the nest (/api/relay/*). Then the nest has no public name, so no website of its own either.
    /// </summary>
    public string? RelayNestId { get; set; }

    /// <summary>The nest key, base64url of 32 bytes (Sync:RelayKey), that the service signs its calls with. A secret: never logged.</summary>
    public string? RelayKey { get; set; }

    /// <summary>The service the apps reach this nest through (Sync:RelayServiceUrl). Told in /api/hello.</summary>
    public string RelayServiceUrl { get; set; } = DefaultRelayServiceUrl;

    /// <summary>Linked to the Pairnets service: both relay settings are set (<see cref="ValidateCommon"/> checks them).</summary>
    public bool RelayMode => RelayNestId is not null && RelayKey is not null;

    /// <summary>The host the relay runs on ("sync.pairnets.app"), for the computers' "approved by …" notes.</summary>
    public string RelayServiceHost =>
        Uri.TryCreate(RelayServiceUrl, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : RelayServiceUrl;

    /// <summary>A nest id as the service makes them (cloud/CONTRACT.md §1.2).</summary>
    public static bool IsNestId(string? id) => id is not null && NestIdPattern().IsMatch(id);

    [GeneratedRegex(@"^nst_[0-9a-hjkmnp-tv-z]{26}\z")]
    private static partial Regex NestIdPattern();

    /// <summary>The domain passkeys are bound to, or null without a public name.</summary>
    public string? EffectiveRpId => string.IsNullOrWhiteSpace(PasskeyRpId) ? PublicHost : PasskeyRpId.Trim().ToLowerInvariant();

    public string EffectiveTlsDir => string.IsNullOrWhiteSpace(TlsDir) ? Path.Combine(DataDir, "tls") : TlsDir;

    /// <summary>The host of PublicUrl ("sync.pairnets.app"), or null.</summary>
    public string? PublicHost =>
        Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) ? uri.IdnHost.ToLowerInvariant() : null;

    public static SyncOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new SyncOptions();
        configuration.GetSection("Sync").Bind(options);
        if (string.IsNullOrEmpty(options.Token))
            options.Token = configuration["SYNC_TOKEN"] ?? string.Empty;
        options.Token = options.Token.Trim();
        // install.sh writes PAIRNETS_PUBLIC_URL into /etc/pairnets/pairnets.env; PUBLIC_URL is the short form.
        if (string.IsNullOrWhiteSpace(options.PublicUrl))
            options.PublicUrl = configuration["PUBLIC_URL"];
        if (string.IsNullOrWhiteSpace(options.PublicUrl))
            options.PublicUrl = configuration["PAIRNETS_PUBLIC_URL"];
        options.PublicUrl = string.IsNullOrWhiteSpace(options.PublicUrl) ? null : options.PublicUrl.Trim().TrimEnd('/');
        options.HttpsUrl = string.IsNullOrWhiteSpace(options.HttpsUrl) ? null : options.HttpsUrl.Trim();
        options.RelayNestId = string.IsNullOrWhiteSpace(options.RelayNestId) ? null : options.RelayNestId.Trim();
        options.RelayKey = string.IsNullOrWhiteSpace(options.RelayKey) ? null : options.RelayKey.Trim();
        options.RelayServiceUrl = string.IsNullOrWhiteSpace(options.RelayServiceUrl) ? DefaultRelayServiceUrl : options.RelayServiceUrl.Trim().TrimEnd('/');
        return options;
    }

    /// <summary>Returns an error message when the options cannot be used to serve requests.</summary>
    public string? ValidateForServe()
    {
        if (Token.Length < MinimumTokenLength)
            return $"Sync:Token (or SYNC_TOKEN) is missing or shorter than {MinimumTokenLength} characters. Generate one with: openssl rand -hex 32";
        return ValidateCommon();
    }

    public string? ValidateCommon()
    {
        if (string.IsNullOrWhiteSpace(DataDir))
            return "Sync:DataDir must be set.";
        if (HistoryRetentionDays < 0)
            return "Sync:HistoryRetentionDays must be >= 0.";
        if (HistoryMinVersions < 0)
            return "Sync:HistoryMinVersions must be >= 0.";
        if (UploadStallTimeout <= TimeSpan.Zero)
            return "Sync:UploadStallTimeout must be greater than zero.";
        if (PublicUrl is not null && (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var pub) || pub.Scheme != Uri.UriSchemeHttps
                || pub.AbsolutePath != "/" || pub.Query.Length > 0 || pub.Fragment.Length > 0))
            return $"Sync:PublicUrl must look like https://sync.example.com (got {PublicUrl}).";
        if (HttpsUrl is not null && (!HttpsUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !TryParseBinding(HttpsUrl, out _, out _)))
            return $"Sync:HttpsUrl must look like https://0.0.0.0:443 (got {HttpsUrl}).";
        if (TlsRecheckInterval < TimeSpan.Zero)
            return "Sync:TlsRecheckInterval must be >= 0.";
        if (SmtpPort is < 1 or > 65535)
            return "Sync:SmtpPort must be between 1 and 65535.";
        if (!string.IsNullOrWhiteSpace(SmtpHost) && string.IsNullOrWhiteSpace(SmtpFrom))
            return "Sync:SmtpFrom (the sender address) is needed with Sync:SmtpHost.";
        if (!Uri.TryCreate(GoogleAuthUrl, UriKind.Absolute, out _) || !Uri.TryCreate(GoogleTokenUrl, UriKind.Absolute, out _))
            return "Sync:GoogleAuthUrl and Sync:GoogleTokenUrl must be web addresses.";
        if (!string.IsNullOrWhiteSpace(PasskeyRpId))
        {
            if (PublicHost is not { } host)
                return "Sync:PasskeyRpId needs Sync:PublicUrl.";
            var rp = PasskeyRpId.Trim().ToLowerInvariant();
            if (host != rp && !host.EndsWith("." + rp, StringComparison.Ordinal))
                return $"Sync:PasskeyRpId ({PasskeyRpId}) must be the nest's host name or a parent domain of it ({host}).";
        }
        return ValidateRelay();
    }

    /// <summary>The relay settings: both or neither, each in its exact shape. The key itself is never repeated in a message.</summary>
    private string? ValidateRelay()
    {
        if ((RelayNestId is null) != (RelayKey is null))
            return "Sync:RelayNestId and Sync:RelayKey go together: set both (install.sh writes them when it links this server to a Pairnets account) or neither.";
        if (!IsServiceUrl(RelayServiceUrl))
            return $"Sync:RelayServiceUrl must look like {DefaultRelayServiceUrl} (got {RelayServiceUrl}).";
        if (RelayNestId is null)
            return null;
        if (!IsNestId(RelayNestId))
            return $"Sync:RelayNestId must be nst_ and 26 lowercase letters and digits, as Pairnets made it (got {RelayNestId}).";
        if (!StrictBase64Url.IsBytes(RelayKey, 32))
            return "Sync:RelayKey must be the key Pairnets gave this server: 43 characters of base64url (32 bytes). Link the server again with: sudo ./install.sh --link";
        if (PublicUrl is not null)
            return $"Sync:PublicUrl ({PublicUrl}) must stay unset on a nest linked to Pairnets (Sync:RelayNestId): it is reached through {RelayServiceUrl} or on its own name, not both.";
        return null;
    }

    /// <summary>https without a path; plain http only to this machine (a test service).</summary>
    private static bool IsServiceUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// Splits a listen address such as "http://100.x.y.z:5075", "https://[::1]:443", "http://localhost:0" or
    /// "http://*:5075" into its host and port. The scheme's default port is used when none is given.
    /// </summary>
    public static bool TryParseBinding(string url, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return false;
        var scheme = url[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("http" or "https"))
            return false;
        var rest = url[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/');
        if (slash >= 0)
            rest = rest[..slash];
        if (rest.Length == 0)
            return false;
        string portText;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
                return false;
            host = rest[1..close];
            portText = rest.Length > close + 1 && rest[close + 1] == ':' ? rest[(close + 2)..] : string.Empty;
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            host = colon >= 0 ? rest[..colon] : rest;
            portText = colon >= 0 ? rest[(colon + 1)..] : string.Empty;
        }
        if (host.Length == 0)
            return false;
        if (portText.Length == 0)
        {
            port = scheme == "https" ? 443 : 80;
            return true;
        }
        return int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            && port is >= 0 and <= 65535;
    }
}
