namespace Pairnets.Server;

/// <summary>Server configuration (section "Sync", environment variables Sync__X, or SYNC_TOKEN).</summary>
public sealed class SyncOptions
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
    /// The server sits behind a tunnel or reverse proxy on this machine (install.sh --cloudflare-tunnel
    /// sets it): take the client's address from CF-Connecting-IP / X-Forwarded-For on loopback
    /// connections. Leave it off when clients connect directly.
    /// </summary>
    public bool TrustProxyHeaders { get; set; }

    public static SyncOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new SyncOptions();
        configuration.GetSection("Sync").Bind(options);
        if (string.IsNullOrEmpty(options.Token))
            options.Token = configuration["SYNC_TOKEN"] ?? string.Empty;
        options.Token = options.Token.Trim();
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
        return null;
    }
}
