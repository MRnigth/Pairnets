using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pairnets.Server.Services;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A website on a local port for nest downloads (1.0.61.5). Its port is let through the nest's address check
/// (<see cref="NestDownloader.TestPorts"/>) while it runs; every other local port stays blocked.
/// <list type="bullet">
/// <item><c>/files/{name}</c>: <see cref="Files"/>, with an ETag and Range; 404 for one not there.</item>
/// <item><c>/named</c>: <see cref="Files"/>["a.bin"] under the name "Holiday video.mp4" (Content-Disposition).</item>
/// <item><c>/unknown/{name}</c>: no length said (chunked).</item>
/// <item><c>/cut/{name}</c>: the first request breaks off half-way; later ones resume with Range.</item>
/// <item><c>/slow/{name}</c>: 64 KB every 50 ms, with Range.</item>
/// <item><c>/redirect/{n}</c>: n redirects, then <c>/files/a.bin</c>; <c>/to/{port}</c>: a redirect to that local port.</item>
/// <item><c>/forbidden</c>: 403.</item>
/// </list>
/// </summary>
public sealed class FakeWebsite : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _cutsLeft;

    private FakeWebsite(WebApplication app) => _app = app;

    /// <summary>The files the website has, by name.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>The Range header of each request, in order ("" for none).</summary>
    public ConcurrentQueue<string> Ranges { get; } = new();

    public int Port { get; private set; }

    public Uri Url(string path) => new($"http://127.0.0.1:{Port}/{path.TrimStart('/')}");

    public static async Task<FakeWebsite> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var site = new FakeWebsite(app) { _cutsLeft = 1 };
        app.Use((ctx, next) =>
        {
            site.Ranges.Enqueue(ctx.Request.Headers.Range.ToString());
            return next(ctx);
        });
        app.MapGet("/files/{name}", (string name) => site.Files.TryGetValue(name, out var bytes) ? Serve(bytes, name) : Results.NotFound());
        app.MapGet("/named", () => Results.File(site.Files["a.bin"], "application/octet-stream", "Holiday video.mp4"));
        app.MapGet("/unknown/{name}", async (string name, HttpContext ctx) =>
        {
            var bytes = site.Files[name];
            ctx.Response.ContentType = "application/octet-stream";
            for (var at = 0; at < bytes.Length; at += 4096)
            {
                await ctx.Response.Body.WriteAsync(bytes.AsMemory(at, Math.Min(4096, bytes.Length - at)));
                await ctx.Response.Body.FlushAsync();
            }
        });
        app.MapGet("/cut/{name}", async (string name, HttpContext ctx) =>
        {
            var bytes = site.Files[name];
            if (ctx.Request.Headers.Range.Count > 0 || Interlocked.Decrement(ref site._cutsLeft) < 0)
            {
                await Serve(bytes, name).ExecuteAsync(ctx);
                return;
            }
            ctx.Response.ContentLength = bytes.Length;
            ctx.Response.Headers.ETag = Tag(name);
            await ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
            await ctx.Response.Body.FlushAsync();
            ctx.Abort();
        });
        app.MapGet("/slow/{name}", async (string name, HttpContext ctx) =>
        {
            var bytes = site.Files[name];
            var from = 0L;
            if (RangeHeaderValue.TryParse(ctx.Request.Headers.Range.ToString(), out var range) && range.Ranges.FirstOrDefault()?.From is { } start)
            {
                from = start;
                ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                ctx.Response.Headers.ContentRange = $"bytes {from}-{bytes.Length - 1}/{bytes.Length}";
            }
            ctx.Response.ContentLength = bytes.Length - from;
            ctx.Response.Headers.ETag = Tag(name);
            for (var at = from; at < bytes.Length; at += 65536)
            {
                await ctx.Response.Body.WriteAsync(bytes.AsMemory((int)at, (int)Math.Min(65536, bytes.Length - at)), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                await Task.Delay(50, ctx.RequestAborted);
            }
        });
        app.MapGet("/redirect/{n:int}", (int n) => Results.Redirect(n <= 1 ? "/files/a.bin" : $"/redirect/{n - 1}"));
        app.MapGet("/to/{port:int}", (int port) => Results.Redirect($"http://127.0.0.1:{port}/files/a.bin"));
        app.MapGet("/forbidden", () => Results.StatusCode(StatusCodes.Status403Forbidden));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        site.Port = new Uri(address).Port;
        NestDownloader.TestPorts[site.Port] = true;
        return site;
    }

    private static IResult Serve(byte[] bytes, string name) =>
        Results.Bytes(bytes, "application/octet-stream", entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(Tag(name)), enableRangeProcessing: true);

    private static string Tag(string name) => "\"" + name + "-v1\"";

    public async ValueTask DisposeAsync()
    {
        NestDownloader.TestPorts.TryRemove(Port, out _);
        await _app.DisposeAsync();
    }
}
