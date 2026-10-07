using Pairnets.Core;
using Microsoft.Extensions.Logging.Console;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;

namespace Pairnets.Server;

/// <summary>Builds the web application. Used by Program and by the in-process integration tests.</summary>
public static class PairnetsServerHost
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

        // Default to loopback only. Production sets Urls / ASPNETCORE_URLS to the Tailscale IP, or keeps
        // 127.0.0.1 behind a Cloudflare Tunnel.
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls(DefaultUrl);

        var options = SyncOptions.FromConfiguration(builder.Configuration);
        var error = options.ValidateForServe();
        if (error is not null)
            throw new InvalidOperationException(error);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new ServerPaths(options.DataDir));
        builder.Services.AddSingleton<SyncStore>();
        builder.Services.AddSingleton<UploadSessions>();
        builder.Services.AddSingleton<FailureThrottle>();
        builder.Services.AddSingleton<ServerUpdater>();
        builder.Services.AddSingleton<BatchRegistry>();
        builder.Services.AddSingleton<DeviceRegistry>();
        builder.Services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = false;
            o.KeepAliveInterval = TimeSpan.FromSeconds(15);
            o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddHostedService<HistoryPurgeService>();
        builder.Services.AddHostedService<UploadSessionSweeper>();
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

        if (options.TrustProxyHeaders)
            app.UseMiddleware<ProxyClientAddressMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.Use((ctx, next) =>
        {
            // Nothing here may be cached or rewritten by a proxy in between (Cloudflare): files,
            // manifests and errors are always fresh and byte-exact.
            ctx.Response.Headers.CacheControl = "no-store, no-transform";
            return next(ctx);
        });
        app.UseWebSockets();
        app.UseMiddleware<TokenAuthMiddleware>();
        var devices = app.Services.GetRequiredService<DeviceRegistry>();
        app.Use(async (ctx, next) =>
        {
            // Authenticated requests only (the auth middleware above answers the rest).
            if (!ctx.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
                devices.Seen(Endpoints.DeviceId(ctx), ClientDescription(ctx.Request));
            await next();
        });
        Endpoints.Map(app);
        app.Lifetime.ApplicationStopped.Register(store.Dispose);
        return app;
    }

    /// <summary>The app's version and system, also from a Tether app that is not updated yet.</summary>
    private static string ClientDescription(HttpRequest request)
    {
        var value = request.Headers[PairnetsHeaders.Client].ToString();
        return value.Length > 0 ? value : request.Headers[Pairnets.Core.Legacy.TetherNames.ClientHeader].ToString();
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
                "files/ and the manifest disagree: {Unknown} unknown, {Missing} missing, {Size} size mismatch, {Invalid} invalid name(s), {Links} symlink(s). Run 'pairnets-server rescan' to reconcile. Examples: {Examples}",
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
                app.Logger.LogWarning("Listening on all interfaces ({Url}). Pairnets should bind only to the Tailscale IP (tailscale ip -4), or to 127.0.0.1 behind a Cloudflare Tunnel.", url);
            }
        }
    }
}
