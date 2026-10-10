using System.Net;
using System.Net.Security;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging.Console;
using Pairnets.Core;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Server.Auth;
using Pairnets.Server.Tls;
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

        // Loopback only: in production the Cloudflare Tunnel on this machine brings the requests in.
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls(DefaultUrl);
        var httpUrls = builder.Configuration["urls"]!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var options = SyncOptions.FromConfiguration(builder.Configuration);
        var error = options.ValidateForServe();
        if (error is not null)
            throw new InvalidOperationException(error);
        if (options.HttpsUrl is not null)
            ListenInCode(builder, httpUrls, options.HttpsUrl);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new ServerPaths(options.DataDir));
        builder.Services.AddSingleton<SyncStore>();
        builder.Services.AddSingleton<UploadSessions>();
        builder.Services.AddSingleton<FailureThrottle>();
        builder.Services.AddSingleton<ServerUpdater>();
        builder.Services.AddSingleton<BatchRegistry>();
        builder.Services.AddSingleton<LiveTransfers>();
        builder.Services.AddSingleton<DeviceRegistry>();
        builder.Services.AddSingleton<TlsCertificateStore>();
        builder.Services.AddSingleton(sp => new AuthStore(sp.GetRequiredService<ServerPaths>()));
        builder.Services.AddSingleton<DeviceKeys>();
        builder.Services.AddSingleton(sp => new RelaySignature(sp.GetRequiredService<SyncOptions>()));
        builder.Services.AddSingleton<OwnerAuth>();
        builder.Services.AddSingleton<Pairnets.Server.Auth.WebAuthn.WebAuthnChallenges>();
        builder.Services.AddSingleton(sp => new GoogleSignIn(sp.GetRequiredService<SyncOptions>()));
        builder.Services.TryAddSingleton<IEmailSender, SmtpEmailSender>(); // a test can register its own first
        builder.Services.AddSingleton<WebUi>();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Joining and signing in work without a key, so one address gets a fair share and no more.
            o.AddPolicy(PairingEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.Services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = false;
            o.KeepAliveInterval = TimeSpan.FromSeconds(15);
            o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddHostedService<HistoryPurgeService>();
        builder.Services.AddHostedService<UploadSessionSweeper>();
        builder.Services.AddHostedService<ConnectionGuardService>();
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
        var auth = app.Services.GetRequiredService<AuthStore>(); // opens (or creates) auth.db now, so a problem stops the start
        // "pairnets-server owner-link" runs without the service's environment; it reads the nest's address from here.
        if (options.PublicUrl is { } publicUrl)
            auth.SetSetting(AuthStore.SettingPublicUrl, publicUrl);
        else
            auth.DeleteSetting(AuthStore.SettingPublicUrl);
        ReportDrift(store, app.Logger);
        WarnAboutBinding(app, options.HttpsUrl is null ? httpUrls : [.. httpUrls, options.HttpsUrl]);
        if (options.HttpsUrl is not null)
            app.Services.GetRequiredService<TlsCertificateStore>().Refresh();
        if (options.RelayMode)
            app.Logger.LogInformation("Linked to Pairnets: reached through {Service} as {NestId}", options.RelayServiceUrl, options.RelayNestId);

        // A linked nest is always behind the service's tunnel on this machine, so it takes the caller's address the same way.
        if (options.TrustProxyHeaders || options.RelayMode)
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
        app.UseMiddleware<RelayAuthMiddleware>(); // /api/relay: the Pairnets service's signed calls, nothing else
        var devices = app.Services.GetRequiredService<DeviceRegistry>();
        app.Use(async (ctx, next) =>
        {
            // Authenticated requests only (the auth middleware above answers the rest).
            if (DeviceIdentity.Of(ctx) is { } identity)
                devices.Seen(identity.RegistryKey, identity.Name, ClientDescription(ctx.Request));
            await next();
        });
        app.UseRateLimiter();
        Endpoints.Map(app);
        PairingEndpoints.Map(app);
        RelayEndpoints.Map(app);
        WebUi.Map(app);
        WebEndpoints.Map(app);
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

    /// <summary>
    /// With a direct HTTPS listener the listeners are set up in code (Kestrel then ignores Urls), so the HTTP
    /// addresses from Urls / ASPNETCORE_URLS are bound here too, followed by the HTTPS one with the renewable
    /// certificate. Behind the Cloudflare Tunnel HttpsUrl is unset and this is not used.
    /// </summary>
    private static void ListenInCode(WebApplicationBuilder builder, IReadOnlyList<string> httpUrls, string httpsUrl)
    {
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var url in httpUrls)
                Listen(kestrel, url, _ => { });
            Listen(kestrel, httpsUrl, listen => listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                OnConnection = _ =>
                {
                    var certificate = kestrel.ApplicationServices.GetRequiredService<TlsCertificateStore>().Current
                        ?? throw new InvalidOperationException("No HTTPS certificate installed yet.");
                    return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificateContext = certificate });
                },
            }));
        });
    }

    private static void Listen(KestrelServerOptions kestrel, string url, Action<ListenOptions> configure)
    {
        if (!SyncOptions.TryParseBinding(url, out var host, out var port))
            throw new InvalidOperationException($"Cannot listen on {url}: expected something like http://127.0.0.1:5075");
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            kestrel.ListenLocalhost(port, configure);
        else if (IPAddress.TryParse(host, out var ip))
            kestrel.Listen(ip, port, configure);
        else
            kestrel.ListenAnyIP(port, configure); // "*", "+" or a host name: Kestrel's own rule for Urls
    }

    private static void WarnAboutBinding(WebApplication app, IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            if (url.Contains("0.0.0.0", StringComparison.Ordinal) || url.Contains("[::]", StringComparison.Ordinal)
                || url.Contains("://*", StringComparison.Ordinal) || url.Contains("://+", StringComparison.Ordinal))
            {
                app.Logger.LogWarning("Listening on all interfaces ({Url}). Pairnets should listen only on 127.0.0.1, behind its Cloudflare Tunnel.", url);
            }
        }
    }
}
