using System.Diagnostics;

namespace Tether.Server.Web;

/// <summary>
/// One log line per request: method, route path, file path parameter, status, duration.
/// Never logs the query string (it may carry access_token) or any header.
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var route = context.Request.Path.Value ?? "/";
            // The apps poll these two; one line each every few seconds would drown the useful ones.
            var level = route.Equals("/api/health", StringComparison.OrdinalIgnoreCase) || route.Equals("/api/devices", StringComparison.OrdinalIgnoreCase)
                ? LogLevel.Debug : LogLevel.Information;
            if (log.IsEnabled(level))
            {
                string? file = context.Request.Query["path"];
                if (file is null)
                    log.Log(level, "{Method} {Route} -> {Status} in {Elapsed:0} ms", context.Request.Method, route, context.Response.StatusCode, elapsed);
                else
                    log.Log(level, "{Method} {Route} {File} -> {Status} in {Elapsed:0} ms", context.Request.Method, route, file, context.Response.StatusCode, elapsed);
            }
        }
    }
}
