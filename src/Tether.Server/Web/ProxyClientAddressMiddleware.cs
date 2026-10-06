using System.Net;

namespace Tether.Server.Web;

/// <summary>
/// Behind a tunnel or reverse proxy on the same machine (cloudflared, Caddy) every request comes from
/// 127.0.0.1, so one stranger trying tokens would make the failure throttle slow down everybody. With
/// Sync:TrustProxyHeaders the client's real address is taken from CF-Connecting-IP (Cloudflare) or the
/// last X-Forwarded-For entry, and only on connections from loopback, so nobody else can fake it.
/// </summary>
public sealed class ProxyClientAddressMiddleware(RequestDelegate next)
{
    public const string CloudflareHeader = "CF-Connecting-IP";

    public Task InvokeAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && IsLoopback(remote) && TryGetClientAddress(context.Request.Headers, out var client))
            context.Connection.RemoteIpAddress = client;
        return next(context);
    }

    /// <summary>The client address a local proxy passed on, if it sent a valid one.</summary>
    public static bool TryGetClientAddress(IHeaderDictionary headers, out IPAddress address)
    {
        var cloudflare = headers[CloudflareHeader].ToString().Trim();
        if (cloudflare.Length > 0)
            return IPAddress.TryParse(cloudflare, out address!);
        // Each proxy appends the address it saw, so the last entry is the one our own proxy added.
        var forwarded = headers["X-Forwarded-For"].ToString();
        var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
        if (last.Length > 0)
            return IPAddress.TryParse(last, out address!);
        address = IPAddress.None;
        return false;
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
