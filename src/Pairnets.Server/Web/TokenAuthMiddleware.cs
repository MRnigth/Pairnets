using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// Authentication for every endpoint except the few public ones (<see cref="IsPublic"/>). A request
/// proves itself with a computer's own key ("pn_…", see <see cref="AuthStore"/>) or, while it is
/// allowed, the old shared token. Either is accepted from X-Sync-Token, "Authorization: Bearer"
/// (SignalR negotiate) or the access_token query parameter (SignalR WebSockets, /hub only). The shared
/// token is compared in constant time over SHA-256 digests; keys are looked up by their SHA-256.
/// Repeated failures from one address are slowed down. Neither is ever logged.
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _expectedDigest;
    private readonly FailureThrottle _throttle;
    private readonly DeviceKeys _keys;
    private readonly ILogger _log;

    public TokenAuthMiddleware(RequestDelegate next, SyncOptions options, FailureThrottle throttle, DeviceKeys keys, ILogger<TokenAuthMiddleware> log)
    {
        _next = next;
        _expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(options.Token));
        _throttle = throttle;
        _keys = keys;
        _log = log;
    }

    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health", // is it up?
        "/api/hello", // is it a Pairnets server, and what is its HTTPS name?
        "/api/pair/start", // a new computer asks to join (the owner approves it on the website)
        "/api/pair/poll", // ...and collects its key with a secret only it knows
        "/", // the nest's website: pages and assets are public; its API checks the signed-in browser itself
    };

    /// <summary>
    /// What needs no key: the endpoints above, the nest's website (<see cref="WebUi"/> pages and assets, and
    /// /web/api, which has its own sign-in), and the Pairnets service's calls under /api/relay, every one of which
    /// must carry the service's signature instead (<see cref="RelayAuthMiddleware"/>). Everything else is denied
    /// without a key or token.
    /// </summary>
    public static bool IsPublic(PathString path) =>
        PublicPaths.Contains(path.Value ?? string.Empty)
        || WebUi.Pages.ContainsKey(path.Value ?? string.Empty)
        || path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase) // sign in with Google (browser redirects)
        || path.StartsWithSegments("/web/api", StringComparison.OrdinalIgnoreCase)
        || RelayAuthMiddleware.IsRelayPath(path); // signed by the service, not anonymous

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsPublic(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var address = context.Connection.RemoteIpAddress ?? IPAddress.None;
        var delay = _throttle.CurrentDelay(address);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, context.RequestAborted);

        var (identity, code, message) = Authenticate(context);
        if (identity is not null)
        {
            _throttle.RecordSuccess(address);
            identity.AttachTo(context);
            await _next(context);
            return;
        }

        var failures = code == ErrorCodes.Unauthorized ? _throttle.RecordFailure(address) : 0;
        _log.LogWarning("Rejected request {Method} {Path} from {Address}: {Reason} ({Failures} recent failure(s))",
            context.Request.Method, context.Request.Path.Value, address, code, failures);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (IsDav(context.Request.Path))
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Pairnets\", charset=\"UTF-8\"";
        await context.Response.WriteAsJsonAsync(new ErrorBody(code, message), PairnetsJson.Options);
    }

    private (DeviceIdentity? Identity, string Code, string Message) Authenticate(HttpContext context)
    {
        // WebDAV (/dav) is a door of its own: only a WebDAV link's password opens it, and that password opens nothing else.
        var dav = IsDav(context.Request.Path);
        var provided = dav ? DavPassword(context.Request) : Provided(context.Request);
        if (string.IsNullOrEmpty(provided))
            return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");

        if (dav || provided.StartsWith(AuthStore.WebDavKeyPrefix, StringComparison.Ordinal))
        {
            var link = dav && provided.StartsWith(AuthStore.WebDavKeyPrefix, StringComparison.Ordinal) ? _keys.Find(provided) : null;
            if (link is not { IsWebDav: true })
                return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");
            if (!link.IsActive)
                return (null, ErrorCodes.DeviceRemoved, "This link was removed from your nest.");
            _keys.Store.TouchWebDavLink(link.Id);
            // Named apart from computers: a link may share a computer's name (names are unique per kind), and a computer skips a
            // push carrying its own name as its own echo, so a link "Mac mini" would hide its changes from the Mac mini.
            return (new DeviceIdentity(link.Id, link.Name + " (WebDAV link)", DeviceAuthKind.WebDavKey, link.AccessOrOwner), string.Empty, string.Empty);
        }

        if (provided.StartsWith(AuthStore.KeyPrefix, StringComparison.Ordinal))
        {
            var device = _keys.Find(provided);
            if (device is null)
                return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");
            if (device.IsWebDav)
                return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");
            if (!device.IsActive)
                return (null, ErrorCodes.DeviceRemoved, "This computer was removed from your nest. Sign in again to keep syncing.");
            return (new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey, device.AccessOrOwner), string.Empty, string.Empty);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        if (!CryptographicOperations.FixedTimeEquals(digest, _expectedDigest))
            return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");
        if (!_keys.AllowSharedToken)
            return (null, ErrorCodes.SharedTokenOff, "The shared token was turned off on your nest. Sign in from Pairnets to keep syncing.");
        return (new DeviceIdentity(null, DeviceIdentity.HeaderName(context), DeviceAuthKind.SharedToken), string.Empty, string.Empty);
    }

    /// <summary>Whether a path is WebDAV's: <c>/dav</c> and everything under it.</summary>
    public static bool IsDav(PathString path) => path.StartsWithSegments("/dav", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The WebDAV password from "Authorization: Basic" (the user name is only a label and is not checked), as the key it is
    /// stored under; null when there is none or it cannot be a password.
    /// </summary>
    private static string? DavPassword(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(auth["Basic ".Length..].Trim()));
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            return AuthStore.WebDavKeyOf(colon < 0 ? text : text[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Provided(HttpRequest request)
    {
        string? provided = request.Headers[PairnetsHeaders.Token];
        if (string.IsNullOrEmpty(provided))
        {
            var auth = request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = auth["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrEmpty(provided) && request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase))
            provided = request.Query["access_token"];
        return string.IsNullOrEmpty(provided) ? null : provided;
    }
}

/// <summary>Per-address failure counter that adds a growing delay after repeated bad tokens.</summary>
public sealed class FailureThrottle
{
    private const int FreeFailures = 5;
    private const int MaxTrackedAddresses = 10_000;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Forget = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTimeOffset Last)> _failures = new();
    private readonly TimeProvider _clock;

    public FailureThrottle(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public TimeSpan CurrentDelay(IPAddress address)
    {
        if (!_failures.TryGetValue(address, out var f))
            return TimeSpan.Zero;
        if (_clock.GetUtcNow() - f.Last > Forget)
        {
            _failures.TryRemove(address, out _);
            return TimeSpan.Zero;
        }
        if (f.Count < FreeFailures)
            return TimeSpan.Zero;
        var seconds = Math.Pow(2, Math.Min(f.Count - FreeFailures, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    public int RecordFailure(IPAddress address)
    {
        if (_failures.Count >= MaxTrackedAddresses)
            Trim();
        var now = _clock.GetUtcNow();
        var updated = _failures.AddOrUpdate(address, _ => (1, now), (_, f) => (now - f.Last > Forget ? 1 : f.Count + 1, now));
        return updated.Count;
    }

    public void RecordSuccess(IPAddress address) => _failures.TryRemove(address, out _);

    private void Trim()
    {
        var now = _clock.GetUtcNow();
        foreach (var (address, f) in _failures)
        {
            if (now - f.Last > Forget)
                _failures.TryRemove(address, out _);
        }
        if (_failures.Count >= MaxTrackedAddresses)
            _failures.Clear();
    }
}
