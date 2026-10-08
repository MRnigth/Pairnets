using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pairnets.Server.Auth.WebAuthn;

namespace Pairnets.Server.Auth;

/// <summary>A Google account as the nest cares about it: Google's stable id for it, and its (verified) address.</summary>
public sealed record GoogleIdentity(string Sub, string Email);

/// <summary>Why a Google sign-in did not work: a short code the website turns into words.</summary>
public sealed class GoogleSignInException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// "Sign in with Google" for the owner: the authorization-code flow with PKCE, using the owner's own Google OAuth
/// client (Sync:GoogleClientId / GoogleClientSecret). The nest never trusts an email address by itself: the account is
/// recognised by Google's stable <c>sub</c> id, which is stored when Google is connected under Security. The identity
/// token comes straight from Google's token endpoint over TLS, so (as OpenID Connect allows) its signature is not
/// re-checked; its issuer, audience, expiry, nonce and verified email are.
/// </summary>
public sealed class GoogleSignIn
{
    public const string Connect = "connect";
    public const string SignIn = "signin";

    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private const int MaxOutstanding = 200;

    private readonly SyncOptions _options;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed record Pending(string Purpose, string Verifier, string Nonce, string? SessionId, string? Next, DateTimeOffset Expires);

    public GoogleSignIn(SyncOptions options, TimeProvider? clock = null, HttpMessageHandler? handler = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string RedirectUri => $"{_options.PublicUrl}/auth/google/callback";

    /// <summary>
    /// Where to send the browser: Google's sign-in page, with a one-time state, PKCE challenge and nonce.
    /// <paramref name="next"/> is a path on the nest to go back to afterwards (already checked by the caller).
    /// </summary>
    public Uri Start(string purpose, string? sessionId, string? next = null)
    {
        var now = _clock.GetUtcNow();
        foreach (var (key, value) in _pending)
        {
            if (value.Expires <= now)
                _pending.TryRemove(key, out _);
        }
        while (_pending.Count >= MaxOutstanding && _pending.MinBy(kv => kv.Value.Expires) is { } oldest)
            _pending.TryRemove(oldest.Key, out _); // the oldest go first, so a flood cannot throw out a sign-in in progress
        var state = WebAuthnVerifier.ToBase64Url(RandomNumberGenerator.GetBytes(18));
        var verifier = WebAuthnVerifier.ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = WebAuthnVerifier.ToBase64Url(RandomNumberGenerator.GetBytes(18));
        _pending[state] = new Pending(purpose, verifier, nonce, sessionId, next, now + StateLifetime);
        var challenge = WebAuthnVerifier.ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.GoogleClientId!,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };
        var separator = _options.GoogleAuthUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return new Uri(_options.GoogleAuthUrl + separator + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
    }

    /// <summary>
    /// Finishes the flow: checks the state, swaps the code for Google's identity token, and checks that. Returns the
    /// purpose the flow was started for, the session it belongs to (connecting needs the signed-in browser), the
    /// path to go back to, and the account.
    /// </summary>
    public async Task<(string Purpose, string? SessionId, string? Next, GoogleIdentity Identity)> FinishAsync(string? state, string? code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending) || pending.Expires <= _clock.GetUtcNow())
            throw new GoogleSignInException("expired", "This Google sign-in expired. Try again.");
        if (string.IsNullOrEmpty(code))
            throw new GoogleSignInException("denied", "Google did not let you in (or you cancelled).");

        TokenResponse? token;
        try
        {
            using var response = await _http.PostAsync(_options.GoogleTokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = _options.GoogleClientId!,
                ["client_secret"] = _options.GoogleClientSecret!,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = pending.Verifier,
            }), ct);
            if (!response.IsSuccessStatusCode)
                throw new GoogleSignInException("failed", "Google did not accept the sign-in. Check the OAuth client settings on the server.");
            token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new GoogleSignInException("failed", "Could not reach Google from the server. Try again.");
        }
        return (pending.Purpose, pending.SessionId, pending.Next, ReadIdToken(token?.IdToken, pending.Nonce));
    }

    private GoogleIdentity ReadIdToken(string? idToken, string nonce)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 } || WebAuthnVerifier.FromBase64Url(parts[1], 8192) is not { } payload)
            throw new GoogleSignInException("failed", "Google's answer was not understood.");
        JsonElement claims;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            claims = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new GoogleSignInException("failed", "Google's answer was not understood.");
        }
        var issuer = Text(claims, "iss");
        if (issuer is not ("https://accounts.google.com" or "accounts.google.com") && !IsTestIssuer(issuer))
            throw new GoogleSignInException("failed", "Google's answer came from an unexpected issuer.");
        if (!Matches(claims, "aud", _options.GoogleClientId!))
            throw new GoogleSignInException("failed", "Google's answer was meant for a different app.");
        if (!claims.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || exp.GetInt64() <= _clock.GetUtcNow().ToUnixTimeSeconds())
            throw new GoogleSignInException("failed", "Google's answer has expired.");
        if (!string.Equals(Text(claims, "nonce"), nonce, StringComparison.Ordinal))
            throw new GoogleSignInException("failed", "Google's answer does not belong to this sign-in.");
        var sub = Text(claims, "sub");
        var email = Text(claims, "email");
        var verified = claims.TryGetProperty("email_verified", out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));
        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email) || !verified)
            throw new GoogleSignInException("failed", "Google did not confirm an email address for that account.");
        return new GoogleIdentity(sub, email);
    }

    /// <summary>With a token URL that is not Google's (tests, a mirror), its issuer is the token URL's own origin.</summary>
    private bool IsTestIssuer(string? issuer) =>
        issuer is not null && !_options.GoogleTokenUrl.StartsWith("https://oauth2.googleapis.com/", StringComparison.Ordinal)
        && _options.GoogleTokenUrl.StartsWith(issuer, StringComparison.Ordinal);

    private static string? Text(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>"aud" is a string or a list of strings.</summary>
    private static bool Matches(JsonElement claims, string name, string expected) =>
        claims.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.String ? value.GetString() == expected
            : value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == expected));

    private sealed record TokenResponse([property: JsonPropertyName("id_token")] string? IdToken);
}
