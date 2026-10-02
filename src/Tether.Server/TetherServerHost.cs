using Microsoft.Extensions.Logging.Console;
using Tether.Server.Services;
using Tether.Server.Storage;
using Tether.Server.Web;

namespace Tether.Server;

/// <summary>Builds the web application. Used by Program and by the in-process integration tests.</summary>
public static class TetherServerHost
{
    public const string DefaultUrl = "http://127.0.0.1:5075";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        ConfigureLogging(builder.Logging);
        configure?.Invoke(builder);

        // Default to loopback only. Production sets Urls / ASPNETCORE_URLS to the Tailscale IP.
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls(DefaultUrl);

        var options = SyncOptions.FromConfiguration(builder.Configuration);
        var error = options.ValidateForServe();
        if (error is not null)
            throw new InvalidOperationException(error);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new ServerPaths(options.DataDir));
        builder.Services.AddSingleton<SyncStore>();
        builder.Services.AddSingleton<FailureThrottle>();
        builder.Services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = false;
            o.KeepAliveInterval = TimeSpan.FromSeconds(15);
            o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddHostedService<HistoryPurgeService>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1024 * 1024; // raised to unlimited on PUT /api/file only
        });

        var app = builder.Build();

        var store = app.Services.GetRequiredService<SyncStore>();
        store.UploadStallTimeout = options.UploadStallTimeout;
        store.Initialize();
        ReportDrift(store, app.Logger);
        WarnAboutBinding(app);

        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseWebSockets();
        app.UseMiddleware<TokenAuthMiddleware>();
        Endpoints.Map(app);
        app.Lifetime.ApplicationStopped.Register(store.Dispose);
        return app;
    }

    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        var underSystemd = Environment.GetEnvironmentVariable("INVOCATION_ID") is not null
            || Environment.GetEnvironmentVariable("JOURNAL_STREAM") is not null;
        if (underSystemd)
            logging.AddSystemdConsole(o => o.UseUtcTimestamp = true);
        else
            logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                o.ColorBehavior = LoggerColorBehavior.Disabled;
            });
    }

    private static void ReportDrift(SyncStore store, ILogger log)
    {
        try
        {
            var drift = store.DetectDrift();
            if (drift.IsClean)
                return;
            log.LogWarning(
                "files/ and the manifest disagree: {Unknown} unknown, {Missing} missing, {Size} size mismatch, {Invalid} invalid name(s), {Links} symlink(s). Run 'tether-server rescan' to reconcile. Examples: {Examples}",
                drift.Unknown.Count, drift.Missing.Count, drift.SizeMismatch.Count, drift.InvalidNames.Count, drift.Symlinks.Count,
                string.Join(", ", drift.Unknown.Concat(drift.Missing).Concat(drift.SizeMismatch).Take(10)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Drift check failed: {Error}", ex.Message);
        }
    }

    private static void WarnAboutBinding(WebApplication app)
    {
        var urls = app.Configuration["urls"] ?? app.Configuration["ASPNETCORE_URLS"] ?? string.Empty;
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (url.Contains("0.0.0.0", StringComparison.Ordinal) || url.Contains("[::]", StringComparison.Ordinal)
                || url.Contains("://*", StringComparison.Ordinal) || url.Contains("://+", StringComparison.Ordinal))
            {
                app.Logger.LogWarning("Listening on all interfaces ({Url}). Tether should bind only to the Tailscale IP (tailscale ip -4).", url);
            }
        }
    }
}
