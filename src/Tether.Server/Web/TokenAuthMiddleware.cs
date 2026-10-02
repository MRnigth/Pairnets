using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tether.Core;

namespace Tether.Server.Web;

/// <summary>
/// Shared-token authentication for every endpoint except /api/health. The token is accepted from
/// X-Sync-Token, "Authorization: Bearer" (SignalR negotiate) or the access_token query parameter
/// (SignalR WebSockets, /hub only). Comparison is constant-time over SHA-256 digests. Repeated
/// failures from one address are slowed down. The token is never logged.
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _expectedDigest;
    private readonly FailureThrottle _throttle;
    private readonly ILogger _log;

    public TokenAuthMiddleware(RequestDelegate next, SyncOptions options, FailureThrottle throttle, ILogger<TokenAuthMiddleware> log)
    {
        _next = next;
        _expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(options.Token));
        _throttle = throttle;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var address = context.Connection.RemoteIpAddress ?? IPAddress.None;
        var delay = _throttle.CurrentDelay(address);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, context.RequestAborted);

        if (IsAuthorized(context.Request))
        {
            _throttle.RecordSuccess(address);
            await _next(context);
            return;
        }

        var failures = _throttle.RecordFailure(address);
        _log.LogWarning("Rejected request {Method} {Path} from {Address}: missing or wrong token ({Failures} recent failure(s))",
            context.Request.Method, context.Request.Path.Value, address, failures);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ErrorBody(ErrorCodes.Unauthorized, "Missing or wrong token."), TetherJson.Options);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        string? provided = request.Headers[TetherHeaders.Token];
        if (string.IsNullOrEmpty(provided))
        {
            var auth = request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                provided = auth["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrEmpty(provided) && request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase))
            provided = request.Query["access_token"];
        if (string.IsNullOrEmpty(provided))
            return false;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(digest, _expectedDigest);
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
