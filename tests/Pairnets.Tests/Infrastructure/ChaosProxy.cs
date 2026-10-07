using System.Net;
using System.Net.Sockets;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A TCP forwarder between a client and the test server that can cut connections after a number
/// of bytes in either direction, to simulate network failures mid-transfer.
/// </summary>
public sealed class ChaosProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _open = [];
    private readonly Task _acceptLoop;

    public ChaosProxy(int targetPort)
    {
        TargetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int TargetPort { get; set; }

    public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    /// <summary>Cut a connection once it has carried this many bytes client → server.</summary>
    public long? CutAfterUpstreamBytes { get; set; }

    /// <summary>Cut a connection once it has carried this many bytes server → client.</summary>
    public long? CutAfterDownstreamBytes { get; set; }

    public int Cuts;

    /// <summary>Resets the cutting rules and drops all open connections.</summary>
    public void Heal()
    {
        CutAfterUpstreamBytes = null;
        CutAfterDownstreamBytes = null;
        lock (_open)
        {
            foreach (var c in _open)
                Abort(c);
            _open.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var server = new TcpClient();
        try
        {
            await server.ConnectAsync(IPAddress.Loopback, TargetPort);
        }
        catch (SocketException)
        {
            Abort(client);
            server.Dispose();
            return;
        }
        lock (_open)
        {
            _open.Add(client);
            _open.Add(server);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var up = PumpAsync(client, server, () => CutAfterUpstreamBytes, cts);
        var down = PumpAsync(server, client, () => CutAfterDownstreamBytes, cts);
        await Task.WhenAny(up, down);
        cts.Cancel();
        Abort(client);
        Abort(server);
        lock (_open)
        {
            _open.Remove(client);
            _open.Remove(server);
        }
    }

    private async Task PumpAsync(TcpClient from, TcpClient to, Func<long?> limit, CancellationTokenSource cts)
    {
        var buffer = new byte[16 * 1024];
        long total = 0;
        try
        {
            var src = from.GetStream();
            var dst = to.GetStream();
            while (!cts.IsCancellationRequested)
            {
                var n = await src.ReadAsync(buffer, cts.Token);
                if (n == 0)
                    return;
                var max = limit();
                if (max is not null && total + n > max)
                {
                    var allowed = (int)Math.Max(0, max.Value - total);
                    if (allowed > 0)
                        await dst.WriteAsync(buffer.AsMemory(0, allowed), cts.Token);
                    Interlocked.Increment(ref Cuts);
                    return; // the caller aborts both sockets: a hard network cut
                }
                total += n;
                await dst.WriteAsync(buffer.AsMemory(0, n), cts.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static void Abort(TcpClient c)
    {
        try
        {
            // Client is null once the connection was closed (Heal and the pump can both get here).
            if (c.Client is { } socket)
                socket.LingerState = new LingerOption(true, 0); // RST, like a dropped link
            c.Close();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Heal();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
