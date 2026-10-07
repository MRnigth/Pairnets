using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pairnets.Server.Auth.WebAuthn;

namespace Pairnets.Tests.Infrastructure;

/// <summary>A stand-in for Google's token endpoint. What it answers is decided by the test; it also checks PKCE.</summary>
public sealed class FakeGoogle : IAsyncDisposable
{
    public const string ClientId = "client-123.apps.googleusercontent.com";
    public const string ClientSecret = "test-oauth-secret";

    private readonly WebApplication _app;

    private FakeGoogle(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public string AuthUrl => Url + "/auth";

    public string TokenUrl => Url + "/token";

    /// <summary>The identity token to return for a code, or null to refuse it.</summary>
    public Func<string, string?> IdTokenFor { get; set; } = _ => null;

    /// <summary>The PKCE challenge the nest sent for the sign-in in progress (set by the test from the redirect).</summary>
    public string? ExpectedChallenge { get; set; }

    public List<string> Requests { get; } = [];

    public static async Task<FakeGoogle> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeGoogle? fake = null;
        app.MapPost("/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            fake!.Requests.Add(string.Join("&", form.Select(kv => $"{kv.Key}={kv.Value}")));
            if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret || form["grant_type"] != "authorization_code")
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            var verifier = form["code_verifier"].ToString();
            var challenge = WebAuthnVerifier.ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            if (fake.ExpectedChallenge is not null && challenge != fake.ExpectedChallenge)
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            return fake.IdTokenFor(form["code"].ToString()) is { } token
                ? Results.Json(new { id_token = token, access_token = "unused", token_type = "Bearer" })
                : Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeGoogle(app, address.TrimEnd('/'));
        return fake;
    }

    /// <summary>An identity token like Google's (its signature is not checked by the nest, see GoogleSignIn).</summary>
    public string IdToken(string sub, string email, string nonce, string? audience = null, string? issuer = null, bool verified = true, long? expires = null) =>
        Jwt(new
        {
            iss = issuer ?? Url,
            aud = audience ?? ClientId,
            sub,
            email,
            email_verified = verified,
            nonce,
            exp = expires ?? DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        });

    private static string Jwt(object claims)
    {
        static string B(object o) => WebAuthnVerifier.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(o));
        return $"{B(new { alg = "RS256", typ = "JWT" })}.{B(claims)}.{WebAuthnVerifier.ToBase64Url([1, 2, 3])}";
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
