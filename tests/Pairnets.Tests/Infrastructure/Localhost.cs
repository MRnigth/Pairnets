using System.Net;
using System.Net.Sockets;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// HTTP clients for the test servers' website at https://localhost:&lt;port&gt;. The servers listen on 127.0.0.1 only,
/// and "localhost" is tried as ::1 first; on Windows each refused attempt there costs about two seconds. So
/// "localhost" goes straight to 127.0.0.1, while the address (and with it the Host header and the cookies) stays
/// "localhost". Certificates are not checked: the test servers make theirs at run time.
/// </summary>
public static class Localhost
{
    public static SocketsHttpHandler Handler(CookieContainer? cookies = null, bool redirects = false) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = redirects,
        UseCookies = cookies is not null,
        CookieContainer = cookies ?? new CookieContainer(),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), ct);
            else
                await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
