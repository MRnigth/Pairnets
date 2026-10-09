using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A stand-in for the Pairnets name service (names/ in the repo) for deploy/pairnets-name.sh: a claim with an emailed
/// code, a new tunnel token, giving the name back. It answers like the real service unless a test says otherwise, and
/// records every request.
/// </summary>
public sealed class FakeNameService : IAsyncDisposable
{
    public const string Code = "123456";
    public const string ClaimId = "pnc_abcdefghijklmnopqrstuvwx";

    /// <summary>Fake values only. The tunnel tokens are built here so no token-shaped text sits in the repo.</summary>
    public static readonly string TunnelToken = "eyJ" + "hIjoi" + new string('Q', 64) + "==";

    public static readonly string NewTunnelToken = "eyJ" + "hIjoi" + new string('R', 64) + "==";

    public static readonly string ManageKey = "pnk_" + new string('k', 43);

    private readonly WebApplication _app;
    private string _name = "";

    private FakeNameService(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public sealed record Recorded(string Method, string Path, string? Authorization, string Body);

    public List<Recorded> Requests { get; } = [];

    /// <summary>The answer to POST /v1/claim/start instead of the normal 202.</summary>
    public (int Status, object Body)? StartAnswer { get; set; }

    /// <summary>The body of the 201 to a right code instead of the normal one.</summary>
    public object? ClaimAnswer { get; set; }

    public static async Task<FakeNameService> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeNameService? fake = null;

        async Task<(JsonElement Body, bool Authorized)> Read(HttpContext ctx)
        {
            var text = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            var authorization = ctx.Request.Headers.Authorization.ToString();
            fake!.Requests.Add(new Recorded(ctx.Request.Method, ctx.Request.Path, authorization.Length > 0 ? authorization : null, text));
            using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
            return (doc.RootElement.Clone(), authorization == "Bearer " + ManageKey);
        }

        static IResult Error(int status, string code, string message) => Results.Json(new { error = code, message }, statusCode: status);

        app.MapPost("/v1/claim/start", async (HttpContext ctx) =>
        {
            var (body, _) = await Read(ctx);
            if (fake!.StartAnswer is { } answer)
                return Results.Json(answer.Body, statusCode: answer.Status);
            fake._name = body.GetProperty("name").GetString()!;
            return Results.Json(new { claim_id = ClaimId, expires_in = 900 }, statusCode: 202);
        });
        app.MapPost("/v1/claim", async (HttpContext ctx) =>
        {
            var (body, _) = await Read(ctx);
            if (body.GetProperty("claim_id").GetString() != ClaimId)
                return Error(400, "expired", "The code has expired. Run the installer again to get a new one.");
            if (body.GetProperty("code").GetString() != Code)
                return Error(400, "wrong_code", "That code is not right (4 tries left).");
            return Results.Json(
                fake!.ClaimAnswer ?? new { name = fake._name, public_url = $"https://{fake._name}.pairnets.app", tunnel_token = TunnelToken, manage_key = ManageKey },
                statusCode: 201);
        });
        app.MapPost("/v1/rotate", async (HttpContext ctx) =>
        {
            var (body, authorized) = await Read(ctx);
            return authorized
                ? Results.Json(new { name = body.GetProperty("name").GetString(), tunnel_token = NewTunnelToken })
                : Error(401, "unauthorized", "That key does not fit this name.");
        });
        app.MapDelete("/v1/name", async (HttpContext ctx) =>
        {
            var (_, authorized) = await Read(ctx);
            return authorized ? Results.NoContent() : Error(401, "unauthorized", "That key does not fit this name.");
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeNameService(app, address.TrimEnd('/'));
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
