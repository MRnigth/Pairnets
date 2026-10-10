namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// Records what an API client sends: every request ("PUT api/upload/…", "GET api/file") with its body length and
/// Range header. It outlives the clients that use it (<see cref="Handler"/> makes a new handler for each), so
/// it also counts across a simulated restart of the app.
/// </summary>
public sealed class RequestLog
{
    private readonly List<Entry> _entries = [];

    /// <summary>Called for each request before it is sent; may throw or block (a test seam).</summary>
    public Action<HttpRequestMessage>? OnRequest { get; set; }

    public sealed record Entry(string Request, long? Length, string? Range, string? IfRange);

    public List<Entry> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    /// <summary>A mark to count from (see <see cref="PieceBytesSince"/>).</summary>
    public int Mark
    {
        get
        {
            lock (_entries)
                return _entries.Count;
        }
    }

    public int Count(string request) => Entries.Count(e => e.Request == request);

    /// <summary>Bytes sent as pieces of uploads ("PUT api/upload/…") since <paramref name="mark"/>.</summary>
    public long PieceBytesSince(int mark) => Entries.Skip(mark).Where(e => e.Request == "PUT api/upload/").Sum(e => e.Length ?? 0);

    public HttpMessageHandler Handler() => new Recording(this);

    private sealed class Recording(RequestLog log) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var name = $"{request.Method} {(path.StartsWith("api/upload/", StringComparison.Ordinal) ? "api/upload/" + path.Split('/').Skip(3).FirstOrDefault() : path)}";
            lock (log._entries)
                log._entries.Add(new Entry(name, request.Content?.Headers.ContentLength, request.Headers.Range?.ToString(), request.Headers.IfRange?.ToString()));
            log.OnRequest?.Invoke(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
