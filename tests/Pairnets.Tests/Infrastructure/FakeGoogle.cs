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

/// <summary>
/// A stand-in for Google's sign-in page and token endpoint, following the rules in docs/PREDEPLOY.md (the Linux test
/// box's fake-google.py follows the same ones). <c>GET /auth</c> signs everyone in as one test identity and packs it
/// into the code; <c>POST /token</c> unpacks it again and checks PKCE. A test can also decide the answer itself
/// with <see cref="IdTokenFor"/>.
/// </summary>
public sealed class FakeGoogle : IAsyncDisposable
{
    public const string ClientId = "client-123.apps.googleusercontent.com";
    public const string ClientSecret = "test-oauth-secret";

    /// <summary>Who <c>GET /auth</c> signs in, unless the query has a <c>login_hint</c>.</summary>
    public const string DefaultSub = "e2e-google-user";

    public const string DefaultEmail = "owner@example.com";

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

    /// <summary>The token requests (form fields), oldest first.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>How many times a browser was sent to the sign-in page (<c>GET /auth</c>).</summary>
    public int AuthVisits => _authVisits;

    private int _authVisits;

    /// <summary>What <c>GET /auth</c> packs into the code it hands back.</summary>
    private sealed record PackedCode(string Sub, string Email, string Nonce, string Challenge);

    public static async Task<FakeGoogle> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeGoogle? fake = null;

        // Google's sign-in page: no questions asked, straight back to the nest with a code.
        app.MapGet("/auth", (HttpContext ctx) =>
        {
            Interlocked.Increment(ref fake!._authVisits);
            var query = ctx.Request.Query;
            var redirect = query["redirect_uri"].ToString();
            if (!Uri.TryCreate(redirect, UriKind.Absolute, out _))
                return Results.BadRequest("redirect_uri is missing");
            var hint = query["login_hint"].ToString();
            var (sub, email) = hint.Contains('@', StringComparison.Ordinal)
                ? ("e2e-" + hint[..hint.IndexOf('@', StringComparison.Ordinal)], hint)
                : (DefaultSub, DefaultEmail);
            var code = WebAuthnVerifier.ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            {
                ["sub"] = sub,
                ["email"] = email,
                ["nonce"] = query["nonce"].ToString(),
                ["challenge"] = query["code_challenge"].ToString(),
            }));
            var separator = redirect.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return Results.Redirect($"{redirect}{separator}state={Uri.EscapeDataString(query["state"].ToString())}&code={code}");
        });

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
            var code = form["code"].ToString();
            if (fake.IdTokenFor(code) is { } token)
                return Results.Json(new { id_token = token, access_token = "unused", token_type = "Bearer" });
            // A code from GET /auth: the identity is inside it, and the verifier must belong to its challenge.
            return Unpack(code) is { } packed && packed.Challenge == challenge
                ? Results.Json(new { id_token = fake.IdToken(packed.Sub, packed.Email, packed.Nonce), access_token = "unused", token_type = "Bearer" })
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

    private static PackedCode? Unpack(string code)
    {
        if (WebAuthnVerifier.FromBase64Url(code, 4096) is not { } json)
            return null;
        try
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return fields is not null && fields.TryGetValue("sub", out var sub) && fields.TryGetValue("email", out var email)
                && fields.TryGetValue("nonce", out var nonce) && fields.TryGetValue("challenge", out var challenge)
                ? new PackedCode(sub, email, nonce, challenge)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
