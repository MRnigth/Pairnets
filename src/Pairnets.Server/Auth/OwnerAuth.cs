using Microsoft.AspNetCore.Identity;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;

namespace Pairnets.Server.Auth;

/// <summary>
/// Signing in to the nest's website: the session cookie, the password, and the checks every website
/// request goes through. The website only runs on the nest's own HTTPS name (Sync:PublicUrl), so the
/// cookie can be <c>__Host-</c>, Secure, HttpOnly and SameSite=Lax, and requests that change something
/// must come from the nest's own pages (Origin check).
/// </summary>
public sealed class OwnerAuth(AuthStore store, SyncOptions options, TimeProvider? clock = null)
{
    public const string CookieName = "__Host-pn_session";
    public const int MinPasswordLength = 10;

    private const string SessionItem = "pairnets.owner";
    private static readonly PasswordHasher<string> Hasher = new();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public AuthStore Store => store;

    /// <summary>Password guesses are checked one at a time (see the sign-in endpoint).</summary>
    public SemaphoreSlim PasswordGate { get; } = new(1, 1);

    public SyncOptions Options => options;

    /// <summary>
    /// The ways the owner can sign in right now. Email and Google count only once they are set up on the server
    /// (SMTP / an OAuth client) <i>and</i> linked to an address or account.
    /// </summary>
    public (bool Password, int Passkeys, bool Email, bool Google) UsableMethods() =>
        (store.PasswordHash is not null, store.CountPasskeys(),
         options.EmailConfigured && store.OwnerEmail is not null,
         options.GoogleConfigured && store.GoogleAccount is not null);

    /// <summary>True when something other than <paramref name="method"/> (password, email, google) would still let the owner in.</summary>
    public bool HasOtherWayThan(string method)
    {
        var (password, passkeys, email, google) = UsableMethods();
        return passkeys > 0 || (method != "password" && password) || (method != "email" && email) || (method != "google" && google);
    }

    /// <summary>
    /// True when the request reached the nest over HTTPS, on the nest's own name (where the website lives).
    /// Behind the Cloudflare Tunnel the server itself speaks HTTP on loopback, so an "https" X-Forwarded-Proto
    /// from the trusted local proxy counts as secure (see <see cref="SyncOptions.TrustProxyHeaders"/>).
    /// </summary>
    public bool IsNestRequest(HttpContext context) =>
        IsSecure(context) && options.PublicHost is { } host && string.Equals(context.Request.Host.Host, host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The request reached the server over HTTPS, directly or through the trusted local proxy (tunnel). The proxy
    /// is recognised by <see cref="ProxyClientAddressMiddleware"/>, not by the client address: that middleware has
    /// already replaced 127.0.0.1 with the browser's own address.
    /// </summary>
    private bool IsSecure(HttpContext context) =>
        context.Request.IsHttps
        || (options.TrustProxyHeaders
            && ProxyClientAddressMiddleware.CameThroughLocalProxy(context)
            && string.Equals(context.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a request that changes something comes from the nest's own pages: Origin (or, without it,
    /// Sec-Fetch-Site) must say same-origin. Blocks other web pages from acting with the owner's cookie.
    /// </summary>
    public bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            var scheme = IsSecure(context) ? "https" : context.Request.Scheme;
            return string.Equals(origin, $"{scheme}://{context.Request.Host.Value}", StringComparison.OrdinalIgnoreCase);
        }
        return context.Request.Headers["Sec-Fetch-Site"].ToString() is "same-origin";
    }

    /// <summary>The signed-in session of this request, or null.</summary>
    public OwnerSession? Current(HttpContext context)
    {
        if (context.Items.TryGetValue(SessionItem, out var cached))
            return cached as OwnerSession;
        var session = context.Request.Cookies.TryGetValue(CookieName, out var secret) && secret is not null ? store.FindSession(secret) : null;
        context.Items[SessionItem] = session;
        return session;
    }

    /// <summary>Signs this browser in and sets its cookie.</summary>
    public OwnerSession SignIn(HttpContext context, string method)
    {
        var (session, secret) = store.CreateSession(method, context.Request.Headers.UserAgent.ToString());
        context.Response.Cookies.Append(CookieName, secret, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = session.Expires,
            IsEssential = true,
        });
        context.Items[SessionItem] = session;
        return session;
    }

    public void SignOut(HttpContext context)
    {
        if (Current(context) is { } session)
            store.DeleteSession(session.Id);
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        context.Items[SessionItem] = null;
    }

    // ------------------------------------------------------------------ password

    public static string? PasswordProblem(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters."
        : password.Length > 200 ? "That is too long (200 characters at most)."
        : null;

    public void SetPassword(string password) => store.SetPasswordHash(Hasher.HashPassword("owner", password));

    public bool CheckPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 200 || store.PasswordHash is not { } hash)
            return false;
        var result = Hasher.VerifyHashedPassword("owner", hash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            SetPassword(password);
        return result != PasswordVerificationResult.Failed;
    }

    /// <summary>The address a browser or app sees the nest at, for links the nest hands out.</summary>
    public string? PublicUrl => options.PublicUrl;

    public DateTimeOffset Now => _clock.GetUtcNow();
}
