using System.Security.Cryptography;
using Pairnets.Server.Auth;

namespace Pairnets.Server.Web;

/// <summary>
/// The nest's website: plain HTML, CSS and JavaScript built into the server program (no files to install,
/// nothing fetched from elsewhere). Pages are served only on the nest's own HTTPS name, with a strict
/// content security policy (no inline scripts or styles, nothing from other sites, no framing).
/// </summary>
public sealed class WebUi
{
    /// <summary>The pages, by path. Each is WebUi/pages/&lt;name&gt;.html.</summary>
    public static readonly IReadOnlyDictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["/setup"] = "setup",
        ["/signin"] = "signin",
        ["/link"] = "link",
        ["/devices"] = "devices",
        ["/security"] = "security",
        ["/email-link"] = "email-link",
    };

    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    private readonly Dictionary<string, Asset> _files = new(StringComparer.Ordinal);

    private sealed record Asset(byte[] Body, string ContentType, string ETag);

    public WebUi()
    {
        var assembly = typeof(WebUi).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith("WebUi/", StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var body = copy.ToArray();
            _files[name["WebUi/".Length..]] = new Asset(body, ContentTypeOf(name), "\"" + Convert.ToHexString(SHA256.HashData(body))[..16] + "\"");
        }
    }

    public bool Has(string path) => _files.ContainsKey(path);

    public static void Map(WebApplication app)
    {
        var web = app.Services.GetRequiredService<WebUi>();

        app.MapGet("/", (HttpContext ctx, OwnerAuth owner) =>
        {
            if (!owner.IsNestRequest(ctx))
                return NotOnNest(ctx, owner);
            var target = owner.Current(ctx) is not null ? "/devices" : owner.Store.HasSignInMethod ? "/signin" : "/setup";
            return Results.Redirect(target);
        });

        foreach (var (path, page) in Pages)
        {
            app.MapGet(path, (HttpContext ctx, OwnerAuth owner) =>
                owner.IsNestRequest(ctx) ? web.Serve(ctx, $"pages/{page}.html", page: true) : NotOnNest(ctx, owner));
        }

        app.MapGet("/assets/{file}", (string file, HttpContext ctx, OwnerAuth owner) =>
            owner.IsNestRequest(ctx) && !file.Contains("..", StringComparison.Ordinal) && web.Has("assets/" + file)
                ? web.Serve(ctx, "assets/" + file, page: false)
                : Results.NotFound());
    }

    private IResult Serve(HttpContext ctx, string name, bool page)
    {
        var asset = _files[name];
        var headers = ctx.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers.CacheControl = page ? "no-store" : "no-cache";
        headers.ETag = asset.ETag;
        if (!page && ctx.Request.Headers.IfNoneMatch.ToString() == asset.ETag)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(asset.Body, asset.ContentType);
    }

    /// <summary>The website only lives on the nest's own name: go there, or explain how to get one.</summary>
    private static IResult NotOnNest(HttpContext ctx, OwnerAuth owner) =>
        owner.PublicUrl is { } url
            ? Results.Redirect(url + ctx.Request.Path + ctx.Request.QueryString)
            : Results.Text("This nest has no website yet. Give it its own name on the server with: sudo ./install.sh --domain nest.example.com",
                "text/plain", statusCode: StatusCodes.Status404NotFound);

    private static string ContentTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };
}
