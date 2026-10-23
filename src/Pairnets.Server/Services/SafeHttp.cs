using System.Net;
using System.Net.Sockets;

namespace Pairnets.Server.Services;

/// <summary>A connection the nest refused to make: the address is inside its own network (<see cref="SafeHttp.IsBlocked"/>).</summary>
public sealed class BlockedAddressException(string host) : IOException($"{host} points inside the nest's own network.");

/// <summary>
/// The web client of nest downloads (1.0.61.5). A link the owner pastes must never make the nest read its own network: the
/// nest itself, the home network, a Tailscale-style 100.64/10 address or a cloud host's metadata address
/// (169.254.169.254). The check runs on the address the client actually connects to, after the name is looked up, so a
/// name that points inside fails too, and so does every redirect (they are followed by hand, see <see cref="NestDownloader"/>).
/// No proxy is used: a proxy would make the connection itself, past this check.
/// </summary>
public static class SafeHttp
{
    /// <summary>A client for downloads. <paramref name="allows"/> (tests only) lets an address and port through the check.</summary>
    public static HttpClient Create(Func<IPAddress, int, bool>? allows = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, allows, ct),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Pairnets/{Pairnets.Core.PairnetsInfo.ProductVersion}");
        return client;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint target, Func<IPAddress, int, bool>? allows, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(target.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new IOException($"{target.Host} has no address.");
        // One inside address is enough to refuse: a name that answers with both could switch between them.
        if (addresses.Any(a => IsBlocked(a) && allows?.Invoke(a, target.Port) != true))
            throw new BlockedAddressException(target.Host);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, target.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// An address the nest never downloads from: loopback, private (10/8, 172.16/12, 192.168/16), shared (100.64/10),
    /// link-local (169.254/16, fe80::/10), unique-local (fc00::/7), multicast, "this network" (0/8, ::) and the reserved
    /// ranges. An IPv4 address inside IPv6 (mapped, NAT64, 6to4) is judged as the IPv4 one.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsBlockedV4(address.GetAddressBytes());
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return true;
        var b = address.GetAddressBytes();
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
            return true;
        if ((b[0] & 0xFE) == 0xFC) // fc00::/7 unique-local
            return true;
        if (b[0] == 0xFE && (b[1] & 0xC0) is 0x80 or 0xC0) // fe80::/10 link-local, fec0::/10 old site-local
            return true;
        if (b[0] == 0xFF) // multicast
            return true;
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0) // 64:ff9b::/96 NAT64
            return IsBlockedV4(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02) // 2002::/16 6to4
            return IsBlockedV4(b[2..6]);
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0) // ::a.b.c.d, the old IPv4-compatible form
            return true;
        return false;
    }

    private static bool IsBlockedV4(byte[] b) =>
        b[0] is 0 or 10 or 127 // this network, private, loopback
        || (b[0] == 100 && (b[1] & 0xC0) == 64) // 100.64/10 shared (Tailscale, carrier NAT)
        || (b[0] == 169 && b[1] == 254) // link-local, the cloud metadata address
        || (b[0] == 172 && (b[1] & 0xF0) == 16) // 172.16/12
        || (b[0] == 192 && b[1] == 168)
        || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // IETF protocol assignments, documentation
        || (b[0] == 198 && (b[1] & 0xFE) == 18) // 198.18/15 benchmarking
        || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113) // documentation
        || b[0] >= 224; // multicast, reserved, broadcast
}
