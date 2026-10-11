using System.Net.Sockets;
using Pairnets.Core.Api;

namespace Pairnets.Tests.Unit;

/// <summary>Every way a connection can fail reads as "cannot reach the server", so a pass ends offline and tries again later.</summary>
public class NetworkErrorTests
{
    /// <summary>
    /// Fails every request the way .NET on Linux can when the server drops a new connection at once: a bare
    /// SocketException ("Transport endpoint is not connected"), not wrapped in an HttpRequestException.
    /// </summary>
    private sealed class DroppingNest : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new SocketException((int)SocketError.NotConnected));
    }

    [Fact]
    public async Task ABareSocketErrorReadsAsCannotReachTheServer()
    {
        using var api = new PairnetsApiClient(new Uri("https://nest.example.com/"), "k", "pc", new DroppingNest());

        await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetInfoAsync(CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.GetManifestAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<PairnetsNetworkException>(() => api.DownloadAsync("a.txt", new MemoryStream(), null, CancellationToken.None));
    }
}
