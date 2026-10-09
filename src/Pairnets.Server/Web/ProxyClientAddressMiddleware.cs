using System.Net;

namespace Pairnets.Server.Web;

/// <summary>
/// Behind a tunnel or reverse proxy on the same machine (cloudflared, Caddy) every request comes from
/// 127.0.0.1, so one stranger trying tokens would make the failure throttle slow down everybody. With
/// Sync:TrustProxyHeaders the client's real address is taken from CF-Connecting-IP (Cloudflare) or the
/// last X-Forwarded-For entry, and only on connections from loopback, so nobody else can fake it.
/// In relay mode (a nest linked to Pairnets, cloud/RELAY.md §3) X-Pairnets-Client-IP comes first: the service sets
/// it to the caller's address as Cloudflare saw it, after removing any value the caller sent.
/// Because the address is replaced, the fact that the connection itself came from this machine is kept
/// separately (<see cref="CameThroughLocalProxy"/>): the nest's website needs it to believe the proxy's
/// X-Forwarded-Proto.
/// </summary>
public sealed class ProxyClientAddressMiddleware(RequestDelegate next, SyncOptions? options = null)
{
    public const string CloudflareHeader = "CF-Connecting-IP";

    /// <summary>The caller's address as the Pairnets service passes it on (relay mode only).</summary>
    public const string RelayClientHeader = "X-Pairnets-Client-IP";

    /// <summary>
    /// HttpContext.Items key, set when the connection itself came from this machine (the tunnel or a local
    /// proxy), before the client address was replaced. Only this middleware sets it; no header can.
    /// </summary>
    public const string LocalProxyItem = "pairnets.via-local-proxy";

    private readonly bool _relay = options?.RelayMode == true;

    public Task InvokeAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        // No address at all (in-memory hosts, Unix sockets) is not a network peer either: it counts as local.
        if (remote is null || IsLoopback(remote))
        {
            context.Items[LocalProxyItem] = true;
            if (remote is not null && TryGetClientAddress(context.Request.Headers, _relay, out var client))
                context.Connection.RemoteIpAddress = client;
        }
        return next(context);
    }

    /// <summary>
    /// True when this request's connection came from this machine (the tunnel or a local proxy), whatever
    /// client address the proxy passed on. Only set while Sync:TrustProxyHeaders (or relay mode) is on.
    /// </summary>
    public static bool CameThroughLocalProxy(HttpContext context) =>
        context.Items.TryGetValue(LocalProxyItem, out var value) && value is true;

    /// <summary>The client address a local proxy passed on, if it sent a valid one.</summary>
    public static bool TryGetClientAddress(IHeaderDictionary headers, out IPAddress address) =>
        TryGetClientAddress(headers, relay: false, out address);

    /// <summary>
    /// The client address a local proxy passed on, if it sent a valid one. With <paramref name="relay"/> the
    /// Pairnets service's X-Pairnets-Client-IP is read first; without it that header means nothing.
    /// </summary>
    public static bool TryGetClientAddress(IHeaderDictionary headers, bool relay, out IPAddress address)
    {
        if (relay)
        {
            var service = headers[RelayClientHeader].ToString().Trim();
            if (service.Length > 0)
                return IPAddress.TryParse(service, out address!);
        }
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
