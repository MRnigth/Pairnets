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
    /// What needs no key: the endpoints above, and the nest's website (<see cref="WebUi"/> pages and assets, and
    /// /web/api, which has its own sign-in). Everything else is denied without a key or token.
    /// </summary>
    public static bool IsPublic(PathString path) =>
        PublicPaths.Contains(path.Value ?? string.Empty)
        || WebUi.Pages.ContainsKey(path.Value ?? string.Empty)
        || path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase) // sign in with Google (browser redirects)
        || path.StartsWithSegments("/web/api", StringComparison.OrdinalIgnoreCase);

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
        await context.Response.WriteAsJsonAsync(new ErrorBody(code, message), PairnetsJson.Options);
    }

    private (DeviceIdentity? Identity, string Code, string Message) Authenticate(HttpContext context)
    {
        var provided = Provided(context.Request);
        if (string.IsNullOrEmpty(provided))
            return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");

        if (provided.StartsWith(AuthStore.KeyPrefix, StringComparison.Ordinal))
        {
            var device = _keys.Find(provided);
            if (device is null)
                return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");
            if (!device.IsActive)
                return (null, ErrorCodes.DeviceRemoved, "This computer was removed from your nest. Sign in again to keep syncing.");
            return (new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey), string.Empty, string.Empty);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        if (!CryptographicOperations.FixedTimeEquals(digest, _expectedDigest))
            return (null, ErrorCodes.Unauthorized, "Missing or wrong token.");
        if (!_keys.AllowSharedToken)
            return (null, ErrorCodes.SharedTokenOff, "The shared token was turned off on your nest. Sign in from Pairnets to keep syncing.");
        return (new DeviceIdentity(null, DeviceIdentity.HeaderName(context), DeviceAuthKind.SharedToken), string.Empty, string.Empty);
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
