using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pairnets.Server.Auth;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A stand-in for the Pairnets service's "add a server" calls (cloud/RELAY.md §2) for deploy/pairnets-link.sh:
/// <c>POST /v1/servers/start</c> and <c>POST /v1/servers/poll</c>. Polls answer from <see cref="PollAnswers"/> in order
/// (the last one repeats); every request is recorded with the time it came in.
/// </summary>
public sealed class FakeRelayService : IAsyncDisposable
{
    public const string UserCode = "ABCD-EFGH";

    public const string Label = "soro";

    /// <summary>Fake values only, built here so no secret-shaped text sits in the repo.</summary>
    public static readonly string DeviceCode = "psd_" + StrictBase64Url.Encode(Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray());

    public static readonly string TunnelToken = "eyJ" + "hIjoi" + new string('Q', 64) + "==";

    public static readonly string NestKey = RelayFixtures.Key;

    private readonly WebApplication _app;

    private FakeRelayService(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public sealed record Recorded(string Method, string Path, string Body, string? ContentType, bool HasOrigin, TimeSpan At);

    public List<Recorded> Requests { get; } = [];

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public int Interval { get; set; } = 1;

    public int ExpiresIn { get; set; } = 900;

    /// <summary>The verificationUriComplete to send instead of this service's own /add page.</summary>
    public string? VerificationLink { get; set; }

    /// <summary>The answer to POST /v1/servers/start instead of the normal 200.</summary>
    public (int Status, object Body)? StartAnswer { get; set; }

    /// <summary>What each poll answers, in order; the last one repeats. Default: approved at once.</summary>
    public Queue<(int Status, object Body)> PollAnswers { get; } = new();

    public object Approved() => new
    {
        status = "approved",
        nestId = RelayFixtures.NestId,
        label = Label,
        relayUrl = $"{Url}/n/{RelayFixtures.NestId}/",
        tunnelToken = TunnelToken,
        nestKey = NestKey,
        keyVersion = 1,
        serviceUrl = Url,
    };

    public static (int, object) Pending => (200, new { status = "pending" });

    public static (int, object) Denied => (200, new { status = "denied" });

    public static (int, object) SlowDown => (429, new { error = "slow_down", message = "Too many requests. Slow down." });

    public static (int, object) Expired => (400, new { error = "expired", message = "This code has expired." });

    public static async Task<FakeRelayService> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeRelayService? fake = null;

        async Task<string> Read(HttpContext ctx)
        {
            var text = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            lock (fake!.Requests)
            {
                fake.Requests.Add(new Recorded(ctx.Request.Method, ctx.Request.Path, text, ctx.Request.ContentType,
                    ctx.Request.Headers.ContainsKey("Origin"), fake._clock.Elapsed));
            }
            return text;
        }

        app.MapPost("/v1/servers/start", async (HttpContext ctx) =>
        {
            await Read(ctx);
            if (fake!.StartAnswer is { } answer)
                return Results.Json(answer.Body, statusCode: answer.Status);
            return Results.Json(new
            {
                deviceCode = DeviceCode,
                userCode = UserCode,
                verificationUri = $"{fake.Url}/add",
                verificationUriComplete = fake.VerificationLink ?? $"{fake.Url}/add?code={UserCode}",
                expiresIn = fake.ExpiresIn,
                interval = fake.Interval,
            });
        });
        app.MapPost("/v1/servers/poll", async (HttpContext ctx) =>
        {
            var body = await Read(ctx);
            if (!body.Contains($"\"deviceCode\":\"{DeviceCode}\"", StringComparison.Ordinal))
                return Results.Json(new { error = "expired", message = "Unknown code." }, statusCode: 400);
            var (status, answer) = fake!.PollAnswers.Count switch
            {
                0 => (200, fake.Approved()),
                1 => fake.PollAnswers.Peek(),
                _ => fake.PollAnswers.Dequeue(),
            };
            return Results.Json(answer, statusCode: status);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeRelayService(app, address.TrimEnd('/'));
        return fake;
    }

    public List<Recorded> Polls()
    {
        lock (Requests)
            return Requests.Where(r => r.Path == "/v1/servers/poll").ToList();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
