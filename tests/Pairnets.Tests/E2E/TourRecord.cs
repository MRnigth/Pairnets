using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Pairnets.Tests.E2E;

/// <summary>One request the server answers, like "GET /api/file". SignalR's own two routes take any method ("ANY /hub").</summary>
public readonly record struct Route(string Method, string Pattern)
{
    public const string AnyMethod = "ANY";

    public override string ToString() => $"{Method} {Pattern}";

    /// <summary>Every route of a running server (one per method), the way <see cref="ApiTour.Routes"/> writes them.</summary>
    public static IReadOnlyList<Route> AllOf(EndpointDataSource source) =>
        source.Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [AnyMethod]).Select(m => new Route(m, PatternOf(e))))
            .Distinct()
            .OrderBy(r => r.Pattern, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)
            .ToList();

    /// <summary>The route a request ended up at on the server.</summary>
    public static Route Of(RouteEndpoint endpoint, string requestMethod) =>
        new(endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is null ? AnyMethod : requestMethod.ToUpperInvariant(), PatternOf(endpoint));

    private static string PatternOf(RouteEndpoint endpoint) => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
}

/// <summary>Tells which of a list of routes a request belongs to. A literal segment wins over a {placeholder}.</summary>
public sealed class RouteTable(IEnumerable<Route> routes)
{
    private readonly List<(Route Route, string[] Segments)> _routes = routes.Select(r => (r, Segments(r.Pattern))).ToList();

    public Route? Match(string method, string path)
    {
        var segments = Segments(path);
        Route? best = null;
        var bestLiterals = -1;
        foreach (var (route, pattern) in _routes)
        {
            if (pattern.Length != segments.Length
                || !(route.Method == Route.AnyMethod || route.Method.Equals(method, StringComparison.OrdinalIgnoreCase)))
                continue;
            var literals = 0;
            var fits = true;
            for (var i = 0; i < pattern.Length && fits; i++)
            {
                if (pattern[i].StartsWith('{'))
                    continue;
                fits = pattern[i].Equals(Uri.UnescapeDataString(segments[i]), StringComparison.OrdinalIgnoreCase);
                literals++;
            }
            if (fits && literals > bestLiterals)
                (best, bestLiterals) = (route, literals);
        }
        return best;
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// What the tour really did: the routes that gave a real answer (not 4xx or 5xx), seen from the tour's side and,
/// where the target can tell, from the server's own side; the hub methods called and events heard; and the
/// command lines run.
/// </summary>
public sealed partial class TourRecord(IEnumerable<Route> claimed)
{
    private readonly ConcurrentDictionary<Route, int> _client = new();
    private readonly ConcurrentDictionary<Route, int> _server = new();
    private readonly ConcurrentDictionary<string, int> _hubCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _hubEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _cliForms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _cliArgs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _cliWords = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _unknown = new();

    public RouteTable Table { get; } = new(claimed);

    /// <summary>Routes that answered the tour's own requests.</summary>
    public IReadOnlySet<Route> ClientAnswered => _client.Keys.ToHashSet();

    /// <summary>Routes the server says it answered (in-process: the endpoint it picked; process: its request log).</summary>
    public IReadOnlySet<Route> ServerAnswered => _server.Keys.ToHashSet();

    public IReadOnlySet<string> HubCalls => _hubCalls.Keys.ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> HubEvents => _hubEvents.Keys.ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> CliForms => _cliForms.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>Every argument given to a command (for the options).</summary>
    public IReadOnlySet<string> CliArgs => _cliArgs.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The first word of every command line that ran ("history", "--help").</summary>
    public IReadOnlySet<string> CliWords => _cliWords.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>Requests that matched none of the claimed routes (shown when something does not add up).</summary>
    public IReadOnlyList<string> Unknown => _unknown.ToList();

    public void ClientSaw(string method, string path, int status) => Saw(_client, method, path, status, "tour");

    public void ServerSaw(Route route, int status)
    {
        if (status < 400)
            _server.AddOrUpdate(route, 1, (_, n) => n + 1);
    }

    public void ServerSaw(string method, string path, int status) => Saw(_server, method, path, status, "server");

    public void HubCalled(string method) => _hubCalls.AddOrUpdate(method, 1, (_, n) => n + 1);

    public void HubHeard(string evt) => _hubEvents.AddOrUpdate(evt, 1, (_, n) => n + 1);

    /// <summary>A command line that ran (an empty one: the server program itself is running).</summary>
    public void CliRan(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
            _cliArgs.TryAdd(arg, 0);
        if (args.Count > 0)
            _cliWords.TryAdd(args[0], 0);
        if (CliUsage.FormOf(args) is { } form)
            _cliForms.AddOrUpdate(form, 1, (_, n) => n + 1);
    }

    /// <summary>Reads the server's request log lines ("GET /api/info -> 200 in 3 ms").</summary>
    public void ServerLogged(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (RequestLine().Match(line) is { Success: true } m)
                ServerSaw(m.Groups["method"].Value, m.Groups["path"].Value, int.Parse(m.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void Saw(ConcurrentDictionary<Route, int> into, string method, string path, int status, string who)
    {
        if (status >= 400)
            return;
        if (Table.Match(method, path) is { } route)
            into.AddOrUpdate(route, 1, (_, n) => n + 1);
        else
            _unknown.Enqueue($"{who}: {method} {path} -> {status}");
    }

    [GeneratedRegex(@"\b(?<method>GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS) (?<path>/\S*)(?: .*?)? -> (?<status>\d{3}) in \d+ ms")]
    private static partial Regex RequestLine();
}

/// <summary>Puts every answer the tour gets into the record (status and route, never the body).</summary>
public sealed class RecordingHandler(TourRecord record, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        record.ClientSaw(request.Method.Method, request.RequestUri!.AbsolutePath, (int)response.StatusCode);
        return response;
    }
}

/// <summary>
/// In-process only: notes the endpoint the server picked for every request and the status it answered with
/// (outermost in the pipeline, so even the requests the sign-in checks turn away are seen).
/// </summary>
public sealed class AnsweredRoutesFilter(Action<Route, int> answered) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            try
            {
                await inner(context);
            }
            finally
            {
                if (context.GetEndpoint() is RouteEndpoint endpoint)
                    answered(Route.Of(endpoint, context.Request.Method), context.Response.StatusCode);
            }
        });
        next(app);
    };
}
