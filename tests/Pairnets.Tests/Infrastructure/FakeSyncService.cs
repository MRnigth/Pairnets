using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pairnets.Core;
using Pairnets.Server.Auth.WebAuthn;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A stand-in for the Pairnets service (cloud/RELAY.md): the apps' account sign-in (<c>/v1/app/start</c>, <c>/poll</c>,
/// <c>/logout</c>) and the relay <c>/n/&lt;nest id&gt;/…</c>, which passes each request on to a real in-process nest with
/// the prefix removed, WebSockets included, like the Worker does. What the sign-in answers is scripted by the test.
/// </summary>
public sealed class FakeSyncService : IAsyncDisposable
{
    /// <summary>The one nest this service relays to (a test-shaped id, CONTRACT §1.2).</summary>
    public const string NestId = "nst_testnest000000000000000001";

    public const string Email = "you@example.com";

    public const string UserCode = "ABCD-EFGH";

    private static readonly string[] HopByHop = ["Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer"];

    private readonly WebApplication _app;
    private readonly TestServer? _nest;
    private readonly HttpClient _forward = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private string? _deviceCode;
    private bool _delivered;
    private int _polls;
    private int _hubSockets;

    private FakeSyncService(WebApplication app, TestServer? nest)
    {
        _app = app;
        _nest = nest;
    }

    /// <summary>The service's address ("http://127.0.0.1:port/"), what the apps are told to use in place of sync.pairnets.app.</summary>
    public Uri Url { get; private set; } = null!;

    /// <summary>The nest's relay address: <c>{Url}n/{NestId}/</c>.</summary>
    public Uri RelayUrl => new(Url, $"n/{NestId}/");

    /// <summary>
    /// What each poll answers, in turn: "pending", "slow_down", "nest_offline", "denied", "expired", "trouble" (a 500),
    /// "approved", or "approved-without-key" (an older service's answer). Once used up, <see cref="AfterScript"/>.
    /// </summary>
    public ConcurrentQueue<string> PollScript { get; } = new();

    public string AfterScript { get; set; } = "approved";

    /// <summary>The link the start answer gives (null: the service's own /app?code= page).</summary>
    public string? VerificationLink { get; set; }

    /// <summary>The server address an approved poll names (null: <see cref="RelayUrl"/>).</summary>
    public string? ServerUrlInAnswer { get; set; }

    /// <summary>False: the relay answers 503 nest_offline, as when the nest's tunnel is down.</summary>
    public bool NestOnline { get; set; } = true;

    /// <summary>False: the relay answers 404 nest_unknown, as after the nest was removed from the account.</summary>
    public bool NestKnown { get; set; } = true;

    /// <summary>
    /// Answers a relayed request instead of the nest (the path as the nest would see it, e.g. "/api/hello"); true when it did.
    /// </summary>
    public Func<HttpContext, string, Task<bool>>? Intercept { get; set; }

    /// <summary>What the app sent to /v1/app/start.</summary>
    public JsonElement? Started { get; private set; }

    public int Polls => Volatile.Read(ref _polls);

    /// <summary>Account tokens handed out, and the ones the app signed out with.</summary>
    public ConcurrentQueue<string> AppTokens { get; } = new();

    public ConcurrentQueue<string> LoggedOut { get; } = new();

    /// <summary>Every relayed request as the nest receives it: "GET /api/info" (no query).</summary>
    public ConcurrentQueue<string> Relayed { get; } = new();

    /// <summary>WebSockets passed through to the nest (the push channel).</summary>
    public int HubSockets => Volatile.Read(ref _hubSockets);

    /// <param name="nest">The nest behind the relay; it hands out the keys of approved sign-ins.</param>
    public static async Task<FakeSyncService> StartAsync(TestServer? nest = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
        var app = builder.Build();
        var fake = new FakeSyncService(app, nest);
        app.UseWebSockets();
        app.MapPost("/v1/app/start", (Delegate)fake.StartAsync);
        app.MapPost("/v1/app/poll", (Delegate)fake.PollAsync);
        app.MapPost("/v1/app/logout", (HttpContext ctx) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.Ordinal))
                fake.LoggedOut.Enqueue(auth["Bearer ".Length..]);
            return Results.NoContent();
        });
        app.Map("/n/{nestId}/{**rest}", fake.RelayAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake.Url = new Uri(address.TrimEnd('/') + "/");
        return fake;
    }

    private static IResult Error(int status, string code, string message = "") =>
        Results.Json(new { error = code, message }, statusCode: status);

    private static string NewSecret(string prefix) => prefix + WebAuthnVerifier.ToBase64Url(RandomNumberGenerator.GetBytes(32));

    private async Task<IResult> StartAsync(HttpContext ctx)
    {
        Started = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        _deviceCode = NewSecret("pcd_");
        _delivered = false;
        var origin = Url.GetLeftPart(UriPartial.Authority);
        return Results.Json(new
        {
            deviceCode = _deviceCode,
            userCode = UserCode,
            verificationUri = origin + "/app",
            verificationUriComplete = VerificationLink ?? $"{origin}/app?code={UserCode}",
            expiresIn = 600,
            interval = 3,
        });
    }

    private async Task<IResult> PollAsync(HttpContext ctx)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        if (_deviceCode is null || !body.TryGetProperty("deviceCode", out var code) || code.GetString() != _deviceCode || _delivered)
            return Error(400, "expired");
        Interlocked.Increment(ref _polls);
        var step = PollScript.TryDequeue(out var next) ? next : AfterScript;
        switch (step)
        {
            case "pending":
                return Results.Json(new { status = "pending" });
            case "denied":
                return Results.Json(new { status = "denied" });
            case "slow_down":
                return Error(429, "slow_down", "Ask less often.");
            case "expired":
                return Error(400, "expired", "That sign-in has expired.");
            case "trouble":
                return Error(500, "server_error", "Something went wrong.");
            case "nest_offline":
                return Error(503, "nest_offline", "Your server is not connected right now. Check that it is on.");
            case "no_nest":
                return Results.Json(new { status = "no_nest", email = Email });
            case "approved-without-key":
            {
                _delivered = true;
                var token = NewSecret("pca_");
                AppTokens.Enqueue(token);
                return Results.Json(new { status = "approved", appToken = token, expiresIn = 3600, email = Email });
            }
            default:
            {
                _delivered = true;
                var name = Started is { } started && started.TryGetProperty("name", out var given) ? given.GetString() ?? "computer" : "computer";
                var grant = _nest?.MintKey(name) ?? new DeviceKeyGrant("dev-1", name, NewSecret("pn_"));
                var token = NewSecret("pca_");
                AppTokens.Enqueue(token);
                return Results.Json(new
                {
                    status = "approved",
                    appToken = token,
                    expiresIn = 3600,
                    email = Email,
                    nest = new { id = NestId, label = "soro", serverUrl = ServerUrlInAnswer ?? RelayUrl.ToString() },
                    device = new { id = grant.Id, name = grant.Name, key = grant.Key },
                });
            }
        }
    }

    // ------------------------------------------------------------------ the relay

    private async Task RelayAsync(HttpContext ctx, string nestId)
    {
        if (nestId != NestId || !NestKnown)
        {
            await Error(404, "nest_unknown", "This server is not linked to Pairnets any more.").ExecuteAsync(ctx);
            return;
        }
        var path = ctx.Request.Path.Value![("/n/" + nestId).Length..];
        if (!(path.StartsWith("/api/", StringComparison.Ordinal) || path == "/hub" || path.StartsWith("/hub/", StringComparison.Ordinal))
            || path.StartsWith("/api/relay/", StringComparison.Ordinal))
        {
            await Error(404, "not_found").ExecuteAsync(ctx);
            return;
        }
        if (!NestOnline || _nest is null)
        {
            await Error(503, "nest_offline", "Your server is not connected right now. Check that it is on.").ExecuteAsync(ctx);
            return;
        }
        Relayed.Enqueue($"{ctx.Request.Method} {path}");
        if (Intercept is { } intercept && await intercept(ctx, path))
            return;
        var target = new Uri(_nest.Url.GetLeftPart(UriPartial.Authority) + path + ctx.Request.QueryString.Value);
        if (ctx.WebSockets.IsWebSocketRequest)
            await ForwardWebSocketAsync(ctx, target);
        else
            await ForwardAsync(ctx, target);
    }

    private static bool Skipped(string header) =>
        HopByHop.Contains(header, StringComparer.OrdinalIgnoreCase)
        || header.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Expect", StringComparison.OrdinalIgnoreCase)
        || header.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || header.StartsWith("X-Pairnets-Client-IP", StringComparison.OrdinalIgnoreCase)
        || header.StartsWith("CF-", StringComparison.OrdinalIgnoreCase);

    private async Task ForwardAsync(HttpContext ctx, Uri target)
    {
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
            req.Content = new StreamContent(ctx.Request.Body);
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (Skipped(name))
                continue;
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                req.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            else
                req.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }
        req.Headers.TryAddWithoutValidation("X-Pairnets-Client-IP", ctx.Connection.RemoteIpAddress?.ToString());
        using var resp = await _forward.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var (name, values) in resp.Headers.Concat(resp.Content.Headers))
        {
            if (!HopByHop.Contains(name, StringComparer.OrdinalIgnoreCase))
                ctx.Response.Headers[name] = values.ToArray();
        }
        await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private async Task ForwardWebSocketAsync(HttpContext ctx, Uri target)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.CollectHttpResponseDetails = true;
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (Skipped(name) || name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            upstream.Options.SetRequestHeader(name, values.ToString());
        }
        foreach (var protocol in ctx.WebSockets.WebSocketRequestedProtocols)
            upstream.Options.AddSubProtocol(protocol);
        try
        {
            await upstream.ConnectAsync(new UriBuilder(target) { Scheme = "ws" }.Uri, ctx.RequestAborted);
        }
        catch (WebSocketException)
        {
            ctx.Response.StatusCode = upstream.HttpStatusCode == 0 ? 502 : (int)upstream.HttpStatusCode;
            return;
        }
        using var downstream = await ctx.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        Interlocked.Increment(ref _hubSockets);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        await Task.WhenAny(PumpAsync(downstream, upstream, stop.Token), PumpAsync(upstream, downstream, stop.Token));
        stop.Cancel();
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var got = await from.ReceiveAsync(buffer, ct);
                if (got.MessageType == WebSocketMessageType.Close)
                {
                    await to.CloseOutputAsync(got.CloseStatus ?? WebSocketCloseStatus.NormalClosure, got.CloseStatusDescription, ct);
                    return;
                }
                await to.SendAsync(buffer.AsMemory(0, got.Count), got.MessageType, got.EndOfMessage, ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _forward.Dispose();
    }
}
