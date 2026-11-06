using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Pairnets.Core;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Server.Auth;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// Folders a WebDAV client made (MKCOL) that hold no file yet. The nest only stores files; a folder is a path prefix, so an
/// empty one would vanish. Finder and Explorer make a folder and then ask about it and put files in, so it is remembered
/// here for <see cref="Lifetime"/> (in memory only: a restart forgets the empty ones, files in them are safe).
/// </summary>
public sealed class DavFolders(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _folders = new(StringComparer.Ordinal);

    public void Add(string path)
    {
        if (_folders.Count > 5000)
            _folders.Clear();
        _folders[path] = _clock.GetUtcNow() + Lifetime;
    }

    public void Remove(string path)
    {
        _folders.TryRemove(path, out _);
        foreach (var key in _folders.Keys)
        {
            if (key.StartsWith(path + "/", StringComparison.Ordinal))
                _folders.TryRemove(key, out _);
        }
    }

    public bool Has(string path) => _folders.TryGetValue(path, out var until) && until > _clock.GetUtcNow();

    /// <summary>The remembered folders at or under <paramref name="path"/> ("" for all), nearest first.</summary>
    public IReadOnlyList<string> Under(string path)
    {
        var now = _clock.GetUtcNow();
        var prefix = path.Length == 0 ? string.Empty : path + "/";
        return _folders.Where(f => f.Value > now && f.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// New files a WebDAV client made empty and has not written into yet. Finder and Explorer make a file with an empty PUT (sometimes
/// after a LOCK) and then PUT its content. Storing the empty one would put a throwaway version in history and push it to every
/// computer, and for a file over 25 MB leave an empty file on every computer when the content then gets 413. So an empty PUT where
/// no file is, is held here instead (in memory, for <see cref="Lifetime"/>) and listed, read, locked, renamed and deleted as if it
/// were there. The PUT with the content stores the file. An empty file nothing is written into is never stored: it goes when its
/// content is refused, after the lifetime, or at a restart (docs/DECISIONS.md, "WebDAV link").
/// </summary>
public sealed class DavEmptyFiles(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most files held at once. Past it the expired ones go, and if that is not enough, all of them: none was stored, so only a name is lost.</summary>
    public const int MaxHeld = 5000;

    /// <summary>A held file's ETag: not a SHA-256, so it never matches a stored version.</summary>
    public const string ETag = "new-empty-file";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Made, DateTimeOffset Until)> _files = new(StringComparer.Ordinal);

    /// <summary>Holds <paramref name="path"/> (again: a second empty PUT keeps when it was made and starts the lifetime over).</summary>
    public void Add(string path)
    {
        var now = _clock.GetUtcNow();
        if (_files.Count >= MaxHeld)
        {
            foreach (var (key, held) in _files)
            {
                if (held.Until <= now)
                    _files.TryRemove(key, out _);
            }
            if (_files.Count >= MaxHeld)
                _files.Clear();
        }
        _files.AddOrUpdate(path, _ => (now, now + Lifetime), (_, old) => (old.Until > now ? old.Made : now, now + Lifetime));
    }

    /// <summary>Starts a held file's lifetime over (a LOCK or PROPPATCH on it: the client is still busy with it).</summary>
    public void Touch(string path)
    {
        var now = _clock.GetUtcNow();
        if (_files.TryGetValue(path, out var held) && held.Until > now)
            _files.TryUpdate(path, (held.Made, now + Lifetime), held);
    }

    /// <summary>When the file held at <paramref name="path"/> was made; null when none is.</summary>
    public DateTimeOffset? MadeAt(string path) => _files.TryGetValue(path, out var held) && held.Until > _clock.GetUtcNow() ? held.Made : null;

    /// <summary>Stops holding <paramref name="path"/>; true when it was held.</summary>
    public bool Remove(string path) => _files.TryRemove(path, out var held) && held.Until > _clock.GetUtcNow();

    /// <summary>Stops holding every file inside folder <paramref name="folder"/>.</summary>
    public void RemoveUnder(string folder)
    {
        foreach (var key in _files.Keys)
        {
            if (key.StartsWith(folder + "/", StringComparison.Ordinal))
                _files.TryRemove(key, out _);
        }
    }

    /// <summary>The held files inside folder <paramref name="path"/> ("" for all), with when each was made.</summary>
    public IReadOnlyList<(string Path, DateTimeOffset Made)> Under(string path)
    {
        var now = _clock.GetUtcNow();
        var prefix = path.Length == 0 ? string.Empty : path + "/";
        return _files.Where(f => f.Value.Until > now && f.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => (f.Key, f.Value.Made)).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>A rename (MOVE): the file held at <paramref name="from"/>, or the held files inside folder <paramref name="from"/>, are held under <paramref name="to"/>.</summary>
    public void Move(string from, string to)
    {
        var now = _clock.GetUtcNow();
        if (_files.TryRemove(from, out var held) && held.Until > now)
            _files[to] = (held.Made, now + Lifetime);
        foreach (var key in _files.Keys)
        {
            if (key.StartsWith(from + "/", StringComparison.Ordinal) && _files.TryRemove(key, out var inner) && inner.Until > now)
                _files[to + key[from.Length..]] = (inner.Made, now + Lifetime);
        }
    }
}

/// <summary>
/// WebDAV (feature/webdav, docs/WEBDAV.md): a Mac's Finder ("Connect to Server") or Windows' "Map network drive" opens the nest's
/// files at <c>/dav</c>. Only a WebDAV link's password opens it (<see cref="TokenAuthMiddleware"/>). Every change goes through
/// <see cref="SyncStore"/>, so the manifest, history, base check and the push channel work as for the apps. Files are put at most
/// <see cref="MaxUploadBytes"/> (25 MB) at a time in one request; bigger ones can be read but not put (413). An encrypted nest
/// has no real files to show, so it answers 403. The link's access (view only or can change, shared folders) is checked here.
/// </summary>
public static class DavEndpoints
{
    /// <summary>The biggest file a WebDAV client may upload (the owner, 2026-10-30): 25 MB.</summary>
    public const long MaxUploadBytes = 25L * 1024 * 1024;

    /// <summary>A folder with more files than this is not deleted or moved in one WebDAV request (the apps' mass-delete guard, in small).</summary>
    public const int MaxFilesPerFolderCall = 100;

    private const string BaseHeader = "X-Pairnets-Dav-Base";
    private const string AllMethods = "OPTIONS, PROPFIND, PROPPATCH, GET, HEAD, PUT, DELETE, MKCOL, MOVE, LOCK, UNLOCK";
    private const string ReadMethods = "OPTIONS, PROPFIND, GET, HEAD";
    // COPY and POST are not mapped: routing answers 405 and Finder and Explorer fall back to reading and putting the file.
    private static readonly string[] Methods = ["OPTIONS", "PROPFIND", "PROPPATCH", "GET", "HEAD", "PUT", "DELETE", "MKCOL", "MOVE", "LOCK", "UNLOCK"];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void Map(WebApplication app)
    {
        MapLinks(app);
        app.MapMethods("/dav/{**rest}", Methods, Handle); // a catch-all may be empty: "/dav" and "/dav/" land here too
    }

    // ------------------------------------------------------------------ the links (the owner's computers)

    public sealed record LinkInfo(string Id, string Label, string Username, string Access, IReadOnlyList<string>? Folders, DateTimeOffset Created, DateTimeOffset? LastUsed);

    public sealed record LinksAnswer([property: System.Text.Json.Serialization.JsonPropertyName("sealed")] bool Sealed, long MaxUploadBytes, IReadOnlyList<LinkInfo> Links);

    /// <summary>The user name shown with a link: its label as a lower-case word ("Mac mini" becomes "mac-mini"). Only a label; the password is the credential.</summary>
    public static string Username(string label)
    {
        var sb = new StringBuilder();
        foreach (var c in label.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
            if (sb.Length >= 32)
                break;
        }
        var name = sb.ToString().Trim('-');
        return name.Length == 0 ? "link" : name;
    }

    private static LinkInfo Info(PairedDevice d) =>
        new(d.Id, d.Name, Username(d.Name), d.AccessOrOwner.Level, d.AccessOrOwner.Folders, d.Created, d.LastUsed);

    /// <summary>The owner's list of links (the apps' <c>GET /api/webdav/links</c> and the nest's website). Never a password or its hash.</summary>
    internal static LinksAnswer Links(AuthStore auth, SyncStore store) =>
        new(store.Sealed, MaxUploadBytes, auth.ListWebDavLinks().Select(Info).ToList());

    /// <summary>
    /// Removes an active WebDAV link (never a computer): its password stops working at once, not after the key cache's 30 seconds.
    /// The removed link, or null when <paramref name="id"/> is no active link.
    /// </summary>
    internal static PairedDevice? RemoveLink(string id, AuthStore auth, DeviceKeys keys)
    {
        if (auth.GetDevice(id) is not { IsWebDav: true, IsActive: true } link || !auth.RemoveDevice(link.Id))
            return null;
        keys.Forget(link.Id);
        return link;
    }

    private static void MapLinks(WebApplication app)
    {
        app.MapGet("/api/webdav/links", (AuthStore auth, SyncStore store) => Results.Json(Links(auth, store), PairnetsJson.Options));

        app.MapPost("/api/webdav/links", async (HttpContext ctx, AuthStore auth, SyncStore store) =>
        {
            if (store.Sealed)
                return Error(StatusCodes.Status409Conflict, "sealed", "This nest is encrypted, so its files can't be shown as a folder in Finder or Explorer.");
            JsonDocument body;
            try
            {
                body = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "The request is not JSON.");
            }
            using (body)
            {
                var root = body.RootElement;
                // "access" is required here (an owner link does not exist): view only or can change.
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access", out var level) || level.ValueKind != JsonValueKind.String
                    || !DeviceAccess.TryRead(root, ownerAllowed: false, out var access))
                    return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Say \"view\" or \"edit\", and optionally 1 to 20 folder names.");
                var label = root.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? AuthStore.CleanName(l.GetString()) : null;
                if (label is null)
                    return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Give the link a name.");
                if (auth.ListWebDavLinks().Count >= AuthStore.MaxWebDavLinks)
                    return Error(StatusCodes.Status409Conflict, "too_many_links", $"A nest keeps at most {AuthStore.MaxWebDavLinks} links. Remove one you don't use first.");
                var (link, password) = auth.AddWebDavLink(label, access);
                var info = Info(link);
                return Results.Json(new { info.Id, info.Label, info.Username, info.Access, info.Folders, info.Created, info.LastUsed, password },
                    PairnetsJson.Options, statusCode: StatusCodes.Status201Created);
            }
        });

        app.MapDelete("/api/webdav/links/{id}", (string id, AuthStore auth, DeviceKeys keys) =>
            RemoveLink(id, auth, keys) is null
                ? Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such link.")
                : Results.NoContent());
    }

    // ------------------------------------------------------------------ one WebDAV request

    private sealed class Dav(HttpContext http, SyncStore store, DeviceAccess access, IReadOnlyList<string> linkedIn, DavFolders folders, DavEmptyFiles emptyFiles, string basePath)
    {
        public HttpContext Http { get; } = http;

        public SyncStore Store { get; } = store;

        public DeviceAccess Access { get; } = access;

        public DavFolders Folders { get; } = folders;

        public DavEmptyFiles EmptyFiles { get; } = emptyFiles;

        /// <summary>The path the client sees the nest's root at ("/dav", or the public link's path): what hrefs start with.</summary>
        public string BasePath { get; } = basePath;

        public bool CanWrite => Access.CanWrite;

        public bool Sees(string path) => AccessGate.Sees(Access, path, linkedIn);

        /// <summary>Whether a path may be changed through this link: inside its share and not in a folder that came in through a link.</summary>
        public bool MayTouch(string path) => Access.Allows(path) && !LinkStore.IsUnder(path, linkedIn);

        public IEnumerable<ManifestEntry> Visible() => Store.ReadManifest(null).Entries.Where(e => !e.Deleted && Sees(e.Path));

        /// <summary>When the empty file held at <paramref name="path"/> (<see cref="DavEmptyFiles"/>) was made, if this link may see it and no stored file has taken its place.</summary>
        public DateTimeOffset? HeldEmpty(string path) =>
            path.Length > 0 && MayTouch(path) && Store.Manifest.Get(path) is not { Deleted: false } ? EmptyFiles.MadeAt(path) : null;

        /// <summary>Whether a held empty file lies inside folder <paramref name="path"/> for this link.</summary>
        public bool HasHeldInside(string path) => EmptyFiles.Under(path).Any(h => MayTouch(h.Path));

        /// <summary>What a listing shows: the stored files this link sees and the empty files held for it, each as a 0-byte file with <see cref="DavEmptyFiles.ETag"/>.</summary>
        public List<ManifestEntry> Listing()
        {
            var entries = Visible().ToList();
            foreach (var (path, made) in EmptyFiles.Under(string.Empty))
            {
                if (HeldEmpty(path) is not null)
                    entries.Add(new ManifestEntry(path, DavEmptyFiles.ETag, 0, made.ToUnixTimeMilliseconds(), Deleted: false, Version: 0));
            }
            return entries;
        }
    }

    private static async Task<IResult> Handle(HttpContext ctx, SyncStore store, IHubContext<SyncHub> hub, DavFolders folders, DavEmptyFiles emptyFiles, LinkStore links, SyncOptions options)
    {
        if (DeviceIdentity.Of(ctx) is not { Kind: DeviceAuthKind.WebDavKey, Access: { } access })
            return Text(StatusCodes.Status401Unauthorized, "Sign in with a WebDAV link's password.");
        if (store.Sealed)
            return Text(StatusCodes.Status403Forbidden, "This nest is encrypted, so its files can't be shown as a folder here. Use the Pairnets app.");

        var dav = new Dav(ctx, store, access, links.LinkedInFolders(), folders, emptyFiles, BasePathOf(ctx, options));
        if (!TryPathOf(ctx, out var path, out var trailingSlash))
            return Text(StatusCodes.Status400BadRequest, "That is not a path in this nest.");
        var method = ctx.Request.Method.ToUpperInvariant();
        if (method is "PROPFIND" or "GET" or "HEAD" && path.Length > 0 && IsJunkName(path))
            return Text(StatusCodes.Status404NotFound, "Not found."); // Finder asks for ._ and .DS_Store all the time: never kept, so no list to read
        // Reading something outside the link's folders answers as if it were not there. A change there is refused (403) by each
        // method's own MayTouch check, the same answer whether or not the file exists, so nothing is given away either way.
        if (method is "PROPFIND" or "GET" or "HEAD" && path.Length > 0 && !dav.Sees(path) && !HasShareInside(dav, path) && !dav.Access.Allows(path))
            return Text(StatusCodes.Status404NotFound, "Not found.");

        switch (method)
        {
            case "OPTIONS":
                ctx.Response.Headers["DAV"] = dav.CanWrite ? "1, 2" : "1";
                ctx.Response.Headers["MS-Author-Via"] = "DAV";
                ctx.Response.Headers.Allow = dav.CanWrite ? AllMethods : ReadMethods;
                return Results.StatusCode(StatusCodes.Status200OK);
            case "PROPFIND":
                return await PropFind(dav, path);
            case "PROPPATCH":
                if (!dav.CanWrite || (path.Length > 0 && !dav.MayTouch(path)))
                    return Text(StatusCodes.Status403Forbidden, "This link can only look at files.");
                if (path.Length > 0)
                    dav.EmptyFiles.Touch(path); // a client setting times on a held empty file is still busy with it
                return PropPatch(dav, path);
            case "GET" or "HEAD":
                return await Get(dav, path, trailingSlash);
            case "PUT":
                return await Put(dav, path, hub);
            case "DELETE":
                return await Delete(dav, path, hub);
            case "MKCOL":
                return await MakeFolder(dav, path);
            case "MOVE":
                return await Move(dav, path, hub);
            case "LOCK":
                if (!dav.CanWrite)
                    return Text(StatusCodes.Status403Forbidden, "This link can only look at files.");
                if (path.Length > 0 && dav.MayTouch(path))
                    dav.EmptyFiles.Touch(path);
                return Lock();
            case "UNLOCK":
                return Results.NoContent();
            default: // not mapped (COPY, POST); kept so a new method can't fall through silently
                return Text(StatusCodes.Status501NotImplemented, "Copy inside the nest isn't supported here; copy the file to your computer and back.");
        }
    }

    /// <summary>A path the link may see because something it is allowed to see lies inside it (the root, or a parent of a shared folder).</summary>
    private static bool HasShareInside(Dav dav, string path) => dav.Access.Folders is { } folders && folders.Any(f => f.StartsWith(path + "/", StringComparison.Ordinal));

    /// <summary>
    /// The nest path a request is about ("" for the root) from what Kestrel decoded, never anything with "..", a backslash or a
    /// drive letter (<see cref="PathRules"/>). False for a path the nest could not hold.
    /// </summary>
    private static bool TryPathOf(HttpContext ctx, out string path, out bool trailingSlash)
    {
        path = string.Empty;
        trailingSlash = false;
        if (HasHiddenSeparatorOrDot(ctx))
            return false;
        var raw = ctx.Request.Path.Value ?? string.Empty;
        raw = raw.Length > 4 ? raw[4..] : string.Empty; // "/dav"
        trailingSlash = raw.EndsWith('/');
        var rest = raw.Trim('/');
        if (rest.Length == 0)
            return true;
        if (rest.Split('/').Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\0')))
            return false;
        if (!PathRules.IsValid(rest))
            return false;
        path = rest;
        return true;
    }

    /// <summary>
    /// Kestrel decodes the path and drops "." and ".." segments before routing, and leaves "%2F" encoded, so "/dav/%2e/x" arrives
    /// as "/dav/x" and "/dav/..%2Fx" as one segment named "..%2Fx". No WebDAV client sends either, so the address as it was sent
    /// is checked: an encoded slash or backslash, or a "." / ".." segment (spelled out or encoded), makes it a bad request.
    /// </summary>
    private static bool HasHiddenSeparatorOrDot(HttpContext ctx)
    {
        var target = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(target))
            target = ctx.Request.Path.Value ?? string.Empty;
        var end = target.IndexOfAny(['?', '#']);
        if (end >= 0)
            target = target[..end];
        if (target.Contains("%2f", StringComparison.OrdinalIgnoreCase) || target.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            return true;
        var dots = target.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
        return dots.Split('/').Any(s => s is "." or "..");
    }

    /// <summary>
    /// The path the client's address ends in: "/dav" on the nest itself, or the public link's path when the Pairnets service
    /// says so in <c>X-Pairnets-Dav-Base</c> (believed in relay mode only, and only on a connection from this machine, the
    /// service's tunnel: the same rule as X-Pairnets-Client-IP in <see cref="ProxyClientAddressMiddleware"/>). The Worker drops any
    /// copy a caller sent and sets its own on every relayed /dav request.
    /// </summary>
    private static string BasePathOf(HttpContext ctx, SyncOptions options)
    {
        if (options.RelayMode && ProxyClientAddressMiddleware.CameThroughLocalProxy(ctx)
            && ctx.Request.Headers[BaseHeader].ToString() is { Length: > 0 and <= 300 } given
            && Uri.TryCreate(given, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.Query.Length == 0)
        {
            var p = url.AbsolutePath.TrimEnd('/');
            if (p.Length > 0 && !p.Contains("..", StringComparison.Ordinal))
                return p;
        }
        return "/dav";
    }

    // ------------------------------------------------------------------ PROPFIND

    private static async Task<IResult> PropFind(Dav dav, string path)
    {
        var depth = dav.Http.Request.Headers["Depth"].ToString();
        if (depth is not ("0" or "1"))
            return Results.Text("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:error xmlns:D=\"DAV:\"><D:propfind-finite-depth/></D:error>",
                "application/xml", Encoding.UTF8, StatusCodes.Status403Forbidden);
        await dav.Http.Request.Body.CopyToAsync(Stream.Null, dav.Http.RequestAborted); // the list of wanted properties: all are always sent

        var entries = dav.Listing();
        var file = path.Length == 0 ? null : entries.FirstOrDefault(e => e.Path == path);
        var isDir = path.Length == 0 || (file is null && IsFolder(dav, entries, path));
        if (file is null && !isDir)
            return Text(StatusCodes.Status404NotFound, "Not found.");

        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
        if (file is not null)
        {
            AppendFile(xml, dav, file);
        }
        else
        {
            var prefix = path.Length == 0 ? string.Empty : path + "/";
            var newest = entries.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(e => e.ModifiedMs).DefaultIfEmpty(0).Max();
            AppendFolder(xml, dav, path, newest);
            if (depth == "1")
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in entries.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var rest = e.Path[prefix.Length..];
                    var slash = rest.IndexOf('/', StringComparison.Ordinal);
                    if (slash < 0)
                    {
                        AppendFile(xml, dav, e);
                    }
                    else if (seen.Add(rest[..slash]))
                    {
                        var folder = prefix + rest[..slash];
                        AppendFolder(xml, dav, folder, entries.Where(x => x.Path.StartsWith(folder + "/", StringComparison.Ordinal)).Select(x => x.ModifiedMs).DefaultIfEmpty(0).Max());
                    }
                }
                foreach (var pending in dav.Folders.Under(path))
                {
                    var rest = pending.Length > prefix.Length ? pending[prefix.Length..] : string.Empty;
                    if (rest.Length > 0 && !rest.Contains('/', StringComparison.Ordinal) && dav.MayTouch(pending) && seen.Add(rest))
                        AppendFolder(xml, dav, pending, 0);
                }
            }
        }
        xml.Append("</D:multistatus>");
        return Results.Text(xml.ToString(), "application/xml", Encoding.UTF8, StatusCodes.Status207MultiStatus);
    }

    private static bool IsFolder(Dav dav, List<ManifestEntry> entries, string path) =>
        entries.Any(e => e.Path.StartsWith(path + "/", StringComparison.Ordinal)) || (dav.Folders.Has(path) && dav.MayTouch(path)) || HasShareInside(dav, path);

    private static void AppendFile(StringBuilder xml, Dav dav, ManifestEntry e)
    {
        var type = ContentTypes.TryGetContentType(e.Path, out var t) ? t : "application/octet-stream";
        xml.Append("<D:response><D:href>").Append(Href(dav, e.Path, folder: false)).Append("</D:href><D:propstat><D:prop>")
            .Append("<D:displayname>").Append(Escape(PathRules.FileName(e.Path))).Append("</D:displayname><D:resourcetype/>")
            .Append("<D:getcontentlength>").Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append("</D:getcontentlength>")
            .Append("<D:getcontenttype>").Append(Escape(type)).Append("</D:getcontenttype>")
            .Append("<D:getlastmodified>").Append(HttpDate(e.ModifiedMs)).Append("</D:getlastmodified>")
            .Append("<D:creationdate>").Append(IsoDate(e.ModifiedMs)).Append("</D:creationdate>")
            .Append("<D:getetag>\"").Append(e.Hash).Append("\"</D:getetag>")
            .Append(SupportedLock(dav))
            .Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
    }

    private static void AppendFolder(StringBuilder xml, Dav dav, string path, long modifiedMs)
    {
        var name = path.Length == 0 ? "/" : PathRules.FileName(path);
        xml.Append("<D:response><D:href>").Append(Href(dav, path, folder: true)).Append("</D:href><D:propstat><D:prop>")
            .Append("<D:displayname>").Append(Escape(name)).Append("</D:displayname><D:resourcetype><D:collection/></D:resourcetype>")
            .Append("<D:getlastmodified>").Append(HttpDate(modifiedMs)).Append("</D:getlastmodified>")
            .Append("<D:creationdate>").Append(IsoDate(modifiedMs)).Append("</D:creationdate>")
            .Append(SupportedLock(dav))
            .Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
    }

    private static string SupportedLock(Dav dav) => dav.CanWrite
        ? "<D:supportedlock><D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry></D:supportedlock>"
        : "<D:supportedlock/>";

    private static string Href(Dav dav, string path, bool folder)
    {
        var sb = new StringBuilder(dav.BasePath);
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            sb.Append('/').Append(Uri.EscapeDataString(segment));
        if (folder || path.Length == 0)
            sb.Append('/');
        return Escape(sb.ToString());
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? string.Empty;

    private static string HttpDate(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(ms, 0)).ToString("R", CultureInfo.InvariantCulture);

    private static string IsoDate(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(ms, 0)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static IResult PropPatch(Dav dav, string path)
    {
        // Finder sets times on what it puts. The nest keeps its own, so the request is answered as done and nothing changes.
        var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>{Href(dav, path, folder: false)}</D:href>"
            + "<D:propstat><D:prop/><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>";
        return Results.Text(xml, "application/xml", Encoding.UTF8, StatusCodes.Status207MultiStatus);
    }

    private static IResult Lock()
    {
        // Finder mounts a share for writing only when it can lock. The nest's own base check (If-Match / the file's hash) is
        // what stops two writers, so a lock here is a formality: it is given, and never holds anyone back.
        var token = "opaquelocktoken:" + Guid.NewGuid().ToString("D");
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:prop xmlns:D=\"DAV:\"><D:lockdiscovery><D:activelock><D:locktype><D:write/></D:locktype>"
            + "<D:lockscope><D:exclusive/></D:lockscope><D:depth>0</D:depth><D:timeout>Second-3600</D:timeout>"
            + $"<D:locktoken><D:href>{token}</D:href></D:locktoken></D:activelock></D:lockdiscovery></D:prop>";
        return new LockResult(token, xml);
    }

    private sealed class LockResult(string token, string xml) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/xml; charset=utf-8";
            httpContext.Response.Headers["Lock-Token"] = "<" + token + ">";
            await httpContext.Response.WriteAsync(xml, Encoding.UTF8, httpContext.RequestAborted);
        }
    }

    // ------------------------------------------------------------------ GET

    private static async Task<IResult> Get(Dav dav, string path, bool trailingSlash)
    {
        if (path.Length == 0 || trailingSlash && dav.Store.Manifest.Get(path) is not { Deleted: false })
            return Text(StatusCodes.Status405MethodNotAllowed, "This is a folder; open it in Finder or Explorer.");
        if (IsJunkName(path) || !dav.Sees(path) || !dav.Access.Allows(path))
            return Text(StatusCodes.Status404NotFound, "Not found.");
        var type = ContentTypes.TryGetContentType(path, out var t) ? t : "application/octet-stream";
        var opened = await dav.Store.OpenReadAsync(path, dav.Http.RequestAborted);
        if (opened is null)
        {
            if (dav.HeldEmpty(path) is { } made) // made empty and not written into yet: 0 bytes, with an ETag no stored version has
                return Results.Bytes([], type, lastModified: made, entityTag: new EntityTagHeaderValue("\"" + DavEmptyFiles.ETag + "\""));
            return dav.Visible().Any(e => e.Path.StartsWith(path + "/", StringComparison.Ordinal)) || dav.HasHeldInside(path)
                ? Text(StatusCodes.Status405MethodNotAllowed, "This is a folder; open it in Finder or Explorer.")
                : Text(StatusCodes.Status404NotFound, "Not found.");
        }
        return Results.File(opened.Stream, type, lastModified: DateTimeOffset.FromUnixTimeMilliseconds(opened.Entry.ModifiedMs),
            entityTag: new EntityTagHeaderValue("\"" + opened.Entry.Hash + "\""), enableRangeProcessing: true);
    }

    // ------------------------------------------------------------------ PUT

    /// <summary>Stops a body at the limit, also when the client did not say how long it is (chunked).</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public bool Exceeded { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _read += n;
            if (_read > limit)
            {
                Exceeded = true;
                throw new InvalidDataException("The upload is over the limit.");
            }
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>How much of a too-big upload is still read (and thrown away) so the client hears the 413: 100 MB.</summary>
    public const long DrainLimitBytes = 4 * MaxUploadBytes;

    /// <summary>
    /// The 413 for a file over <see cref="MaxUploadBytes"/>. Answering while the client is still sending makes the connection drop,
    /// and Finder and Explorer then show a vague network error instead of "over 25 MB". So the rest of the body is read and thrown
    /// away first, up to <see cref="DrainLimitBytes"/>. A client that sent "Expect: 100-continue" has sent nothing yet and is
    /// answered at once, and so is one that says it is sending more than the drain limit.
    /// </summary>
    private static async Task<IResult> TooLargeAsync(HttpContext http)
    {
        var waiting = http.Request.Headers.Expect.ToString().Contains("100-continue", StringComparison.OrdinalIgnoreCase);
        if (!waiting && http.Request.ContentLength is null or <= DrainLimitBytes)
        {
            try
            {
                await http.Request.Body.CopyToAsync(Stream.Null, http.RequestAborted);
            }
            catch (Exception ex) when (ex is BadHttpRequestException or IOException or OperationCanceledException)
            {
                // over the drain limit or gone: the answer below is all that is left to try
            }
        }
        return Text(StatusCodes.Status413PayloadTooLarge, "That file is over 25 MB. Files bigger than 25 MB can't be added this way; use the Pairnets app.");
    }

    private static async Task<IResult> Put(Dav dav, string path, IHubContext<SyncHub> hub)
    {
        var http = dav.Http;
        if (!dav.CanWrite)
            return Text(StatusCodes.Status403Forbidden, "This link can only look at files.");
        if (path.Length == 0)
            return Text(StatusCodes.Status405MethodNotAllowed, "That is a folder.");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = DrainLimitBytes; // LimitedStream below stops the file itself at MaxUploadBytes
        if (http.Request.ContentLength > MaxUploadBytes)
        {
            if (dav.MayTouch(path))
                dav.EmptyFiles.Remove(path); // the content of a held empty file was refused: it goes too, so nothing is left behind
            return await TooLargeAsync(http); // before a byte is stored
        }
        if (IsJunkName(path))
        {
            // Finder's ._ files and the like are never synced; accept and drop them so the copy goes on.
            await http.Request.Body.CopyToAsync(Stream.Null, http.RequestAborted);
            return Results.StatusCode(StatusCodes.Status201Created);
        }
        if (!dav.MayTouch(path))
            return Text(StatusCodes.Status403Forbidden, "This file is not in the folders shared with this link.");

        var current = dav.Store.Manifest.Get(path) is { Deleted: false } live ? live : null;
        var held = current is null && dav.EmptyFiles.MadeAt(path) is not null;
        if (current is null && (dav.Visible().Any(e => e.Path.StartsWith(path + "/", StringComparison.Ordinal)) || dav.HasHeldInside(path)))
            return Text(StatusCodes.Status405MethodNotAllowed, "That is a folder.");
        foreach (var parent in PathRules.DirectoryPrefixes(path))
        {
            if (dav.Store.Manifest.Get(parent) is { Deleted: false } || dav.HeldEmpty(parent) is not null)
                return Text(StatusCodes.Status409Conflict, "A file with the name of the folder is in the way.");
        }

        // The base check as the apps do it: If-Match means "only if it is still this version" (412 when it is not), If-None-Match: *
        // "only if it is new". With neither, the client means "replace": the current version is the base, and the one it replaces
        // is kept in the nest's history. A held empty file counts as there, with no stored version: its ETag matches like "*".
        var ifMatchHeader = http.Request.Headers.IfMatch.ToString();
        var ifMatch = held && IsHeldETag(ifMatchHeader) ? "*" : ETagOf(ifMatchHeader);
        if (ifMatch is "invalid")
            return Text(StatusCodes.Status412PreconditionFailed, "That is not a version of this file.");
        var onlyIfNew = http.Request.Headers.IfNoneMatch.ToString().Trim() == "*";
        var conditional = ifMatch is not null || onlyIfNew;
        if (onlyIfNew && (current is not null || held))
            return Text(StatusCodes.Status412PreconditionFailed, "The file already exists.");
        var baseValue = onlyIfNew ? "none" : ifMatch ?? current?.Hash ?? "none";
        if (ifMatch is "*")
        {
            if (current is null && !held)
                return Text(StatusCodes.Status412PreconditionFailed, "The file does not exist.");
            baseValue = current?.Hash ?? "none";
        }

        // A new file made empty is held, not stored (DavEmptyFiles): Finder and Explorer PUT the content next. An empty PUT over a
        // stored file is not held: that is "make this file empty", stored like any change.
        if (current is null && HasNoBody(http))
        {
            if (baseValue != "none")
                return Text(StatusCodes.Status412PreconditionFailed, "The file changed on the nest in the meantime.");
            dav.EmptyFiles.Add(path);
            http.Response.Headers.ETag = "\"" + DavEmptyFiles.ETag + "\"";
            return Results.StatusCode(held ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
        }

        var limited = new LimitedStream(http.Request.Body, MaxUploadBytes);
        ChangeResult result;
        try
        {
            result = await dav.Store.PutAsync(path, baseValue, null, limited, http.RequestAborted);
        }
        catch (Exception) when (limited.Exceeded)
        {
            dav.EmptyFiles.Remove(path); // as above: a held empty file whose content was refused is not left behind
            return await TooLargeAsync(http);
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Text(StatusCodes.Status408RequestTimeout, "The upload stalled and was dropped.");
        }
        switch (result.Status)
        {
            case ChangeStatus.Ok:
                await Endpoints.BroadcastAsync(http, hub, path);
                http.Response.Headers.ETag = "\"" + result.Entry!.Hash + "\"";
                dav.Folders.Remove(path);
                dav.EmptyFiles.Remove(path);
                return Results.StatusCode(current is null && !held ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
            case ChangeStatus.Unchanged:
                return Results.StatusCode(StatusCodes.Status204NoContent);
            case ChangeStatus.Conflict:
                return conditional
                    ? Text(StatusCodes.Status412PreconditionFailed, "The file changed on the nest in the meantime.")
                    : Text(StatusCodes.Status409Conflict, "The file changed on the nest while it was being saved. Try again.");
            default:
                return Text(result.Status == ChangeStatus.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict, result.Message ?? "The nest refused this file.");
        }
    }

    private static string? ETagOf(string header)
    {
        var value = header.Trim();
        if (value.Length == 0)
            return null;
        if (value == "*")
            return "*";
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];
        value = value.Trim('"');
        return ContentHash.IsValid(value) ? value : "invalid"; // "invalid" never matches a version, so the write is refused
    }

    private static bool IsHeldETag(string header)
    {
        var value = header.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
            value = value[2..];
        return value.Trim('"') == DavEmptyFiles.ETag;
    }

    /// <summary>A request that says it carries nothing: <c>Content-Length: 0</c>, or neither a length nor chunks.</summary>
    private static bool HasNoBody(HttpContext http) =>
        http.Request.ContentLength == 0 || http.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false };

    /// <summary>
    /// The folder metadata Finder and Explorer write everywhere (._ files, .DS_Store, Thumbs.db, desktop.ini): accepted and dropped.
    /// Not the apps' whole ignore list: Word and LibreOffice save through "~WRD0000.tmp" / "lu123.tmp" and a rename onto the
    /// document, so dropping *.tmp or ~$* would lose the save while the program thinks it worked. Those are kept; the apps skip
    /// them (SyncEngine ignores what its ignore list names), and the program deletes them when it is done.
    /// </summary>
    private static bool IsJunkName(string path)
    {
        var name = PathRules.FileName(path);
        return name.StartsWith("._", StringComparison.Ordinal)
            || name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".localized", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ DELETE, MKCOL, MOVE

    private static async Task<IResult> Delete(Dav dav, string path, IHubContext<SyncHub> hub)
    {
        if (!dav.CanWrite)
            return Text(StatusCodes.Status403Forbidden, "This link can only look at files.");
        if (path.Length == 0)
            return Text(StatusCodes.Status403Forbidden, "The nest itself can't be deleted.");
        if (IsJunkName(path))
            return Results.NoContent();
        if (!dav.MayTouch(path))
            return Text(StatusCodes.Status403Forbidden, "This is not in the folders shared with this link.");

        if (dav.Store.Manifest.Get(path) is { Deleted: false } file)
        {
            var ifMatch = ETagOf(dav.Http.Request.Headers.IfMatch.ToString());
            if (ifMatch is "invalid")
                return Text(StatusCodes.Status412PreconditionFailed, "That is not a version of this file.");
            var result = await dav.Store.DeleteAsync(path, ifMatch is null or "*" ? file.Hash! : ifMatch);
            if (result.Status == ChangeStatus.Ok)
            {
                await Endpoints.BroadcastAsync(dav.Http, hub, path);
                dav.EmptyFiles.Remove(path);
            }
            return result.Status switch
            {
                ChangeStatus.Ok or ChangeStatus.Unchanged or ChangeStatus.NotFound => Results.NoContent(),
                ChangeStatus.Conflict => Text(StatusCodes.Status412PreconditionFailed, "The file changed on the nest in the meantime."),
                _ => Text(StatusCodes.Status409Conflict, result.Message ?? "The nest refused this."),
            };
        }

        if (dav.HeldEmpty(path) is not null)
        {
            dav.EmptyFiles.Remove(path); // a held empty file was never stored: nothing to keep in history, nothing to tell the computers
            return Results.NoContent();
        }

        var files = dav.Visible().Where(e => e.Path.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
        if (files.Count == 0)
        {
            if (!dav.Folders.Has(path) && !dav.HasHeldInside(path))
                return Text(StatusCodes.Status404NotFound, "Not found.");
            dav.Folders.Remove(path);
            dav.EmptyFiles.RemoveUnder(path);
            return Results.NoContent();
        }
        if (files.Count > MaxFilesPerFolderCall)
            return Text(StatusCodes.Status403Forbidden, $"That folder holds more than {MaxFilesPerFolderCall} files. Delete it in the Pairnets app, which asks first.");
        dav.EmptyFiles.RemoveUnder(path);
        var failed = new List<(string Path, int Status)>();
        foreach (var e in files)
        {
            var result = await dav.Store.DeleteAsync(e.Path, e.Hash!);
            if (result.Status == ChangeStatus.Ok)
                await Endpoints.BroadcastAsync(dav.Http, hub, e.Path);
            else if (result.Status is not (ChangeStatus.Unchanged or ChangeStatus.NotFound))
                failed.Add((e.Path, result.Status == ChangeStatus.Conflict ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status409Conflict));
        }
        if (failed.Count > 0)
            return PartlyDone(dav, failed); // a file changed meanwhile stays, and the client is told which
        dav.Folders.Remove(path);
        return Results.NoContent();
    }

    /// <summary>207 for a folder DELETE or MOVE that left some files behind: each one with why (RFC 4918 9.6.1).</summary>
    private static IResult PartlyDone(Dav dav, List<(string Path, int Status)> failed)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
        foreach (var (path, status) in failed)
        {
            var reason = status == StatusCodes.Status412PreconditionFailed ? "Precondition Failed" : "Conflict";
            xml.Append("<D:response><D:href>").Append(Href(dav, path, folder: false)).Append("</D:href><D:status>HTTP/1.1 ")
                .Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("</D:status></D:response>");
        }
        xml.Append("</D:multistatus>");
        return Results.Text(xml.ToString(), "application/xml", Encoding.UTF8, StatusCodes.Status207MultiStatus);
    }

    private static Task<IResult> MakeFolder(Dav dav, string path)
    {
        if (!dav.CanWrite)
            return Task.FromResult(Text(StatusCodes.Status403Forbidden, "This link can only look at files."));
        if (dav.Http.Request.ContentLength is > 0)
            return Task.FromResult(Text(StatusCodes.Status415UnsupportedMediaType, "A folder has no content."));
        if (path.Length == 0 || IsJunkName(path) || !dav.MayTouch(path))
            return Task.FromResult(Text(path.Length == 0 ? StatusCodes.Status405MethodNotAllowed : StatusCodes.Status403Forbidden, "This folder can't be made here."));
        var entries = dav.Listing();
        if (dav.Store.Manifest.Get(path) is { Deleted: false } || dav.HeldEmpty(path) is not null || IsFolder(dav, entries, path))
            return Task.FromResult(Text(StatusCodes.Status405MethodNotAllowed, "It already exists."));
        var parent = PathRules.Parent(path) ?? string.Empty;
        if (parent.Length > 0 && (dav.Store.Manifest.Get(parent) is { Deleted: false } || dav.HeldEmpty(parent) is not null || !IsFolder(dav, entries, parent)))
            return Task.FromResult(Text(StatusCodes.Status409Conflict, "The folder above does not exist."));
        dav.Folders.Add(path);
        return Task.FromResult(Results.StatusCode(StatusCodes.Status201Created));
    }

    private static async Task<IResult> Move(Dav dav, string from, IHubContext<SyncHub> hub)
    {
        var http = dav.Http;
        if (!dav.CanWrite)
            return Text(StatusCodes.Status403Forbidden, "This link can only look at files.");
        if (from.Length == 0)
            return Text(StatusCodes.Status403Forbidden, "The nest itself can't be moved.");
        if (!TryDestination(dav, out var to))
            return Text(StatusCodes.Status400BadRequest, "The destination is not in this nest.");
        if (to.Length == 0 || to == from || to.StartsWith(from + "/", StringComparison.Ordinal) || !PathRules.IsValid(to))
            return Text(StatusCodes.Status403Forbidden, "That can't be moved there.");
        if (IsJunkName(from) && IsJunkName(to))
            return Results.StatusCode(StatusCodes.Status201Created); // metadata renamed to metadata: nothing was kept, nothing to move
        if (IsJunkName(to))
            return Text(StatusCodes.Status403Forbidden, "Files with that name aren't kept in the nest."); // never answer "done" and keep nothing
        // a move from a metadata name to a real one finds no source below (none was kept) and answers 404
        if (!dav.MayTouch(from) || !dav.MayTouch(to))
            return Text(StatusCodes.Status403Forbidden, "This is not in the folders shared with this link.");
        var overwrite = !string.Equals(http.Request.Headers["Overwrite"].ToString().Trim(), "F", StringComparison.OrdinalIgnoreCase);

        var entries = dav.Visible().ToList();
        var source = entries.FirstOrDefault(e => e.Path == from);
        if (source is null && dav.HeldEmpty(from) is not null)
            return await MoveHeldAsync(dav, from, to, overwrite, hub);
        var sources = source is not null
            ? [source]
            : entries.Where(e => e.Path.StartsWith(from + "/", StringComparison.Ordinal)).ToList();
        if (sources.Count == 0)
        {
            if (!dav.Folders.Has(from) && !dav.HasHeldInside(from))
                return Text(StatusCodes.Status404NotFound, "Not found.");
            dav.Folders.Remove(from);
            dav.Folders.Add(to);
            dav.EmptyFiles.Move(from, to);
            return Results.StatusCode(StatusCodes.Status201Created);
        }
        if (sources.Count > MaxFilesPerFolderCall)
            return Text(StatusCodes.Status403Forbidden, $"That folder holds more than {MaxFilesPerFolderCall} files. Move it in the Pairnets app.");
        if (PathRules.CaseKey(from) == PathRules.CaseKey(to))
            return await RenameCaseAsync(dav, from, to, sources, folder: source is null, hub);

        var targets = sources.Select(s => (From: s, To: source is not null ? to : to + s.Path[from.Length..])).ToList();
        var existed = false;
        foreach (var (_, target) in targets)
        {
            if (!PathRules.IsValid(target) || !dav.MayTouch(target))
                return Text(StatusCodes.Status403Forbidden, "That can't be moved there.");
            if (dav.Store.Manifest.Get(target) is { Deleted: false } || dav.HeldEmpty(target) is not null)
            {
                existed = true;
                if (!overwrite)
                    return Text(StatusCodes.Status412PreconditionFailed, "There is already something with that name.");
            }
        }
        var failed = new List<(string Path, int Status)>();
        foreach (var (src, target) in targets)
        {
            // An existing target is replaced by the move itself (its old version goes to history), never deleted first:
            // a move that then failed would have left it gone.
            var replacing = dav.Store.Manifest.Get(target) is { Deleted: false } old ? old.Hash : null;
            var result = await MoveOneAsync(dav, src.Path, target, src.Hash!, replacing);
            var status = result.Status == ChangeStatus.Conflict ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status409Conflict;
            if (result.Status != ChangeStatus.Ok)
            {
                if (source is not null)
                    return Text(status, result.Message ?? "The nest refused this.");
                failed.Add((src.Path, status)); // the rest of the folder still moves; the client hears which files stayed
                continue;
            }
            await Endpoints.BroadcastAsync(http, hub, src.Path);
            await Endpoints.BroadcastAsync(http, hub, target);
            dav.EmptyFiles.Remove(target); // a held empty file of that name is replaced by the stored one
        }
        if (failed.Count > 0)
            return PartlyDone(dav, failed);
        if (source is null)
        {
            dav.Folders.Remove(from);
            dav.Folders.Add(to);
            dav.EmptyFiles.Move(from, to);
        }
        return Results.StatusCode(existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
    }

    /// <summary>One file through <see cref="SyncStore.MoveAsync"/>; a problem on the disk (e.g. a file where a folder of the target should be) is a refusal.</summary>
    private static async Task<ChangeResult> MoveOneAsync(Dav dav, string from, string to, string hash, string? replacing = null)
    {
        try
        {
            return await dav.Store.MoveAsync(from, to, hash, CancellationToken.None, replacing);
        }
        catch (IOException ex)
        {
            return new ChangeResult(ChangeStatus.BadRequest, null, ex.Message);
        }
    }

    /// <summary>What the passing name of a rename by letter case adds to the old name: "photo.jpg.pairnets-rename-1f3a9c2e".</summary>
    public const string PassingMark = ".pairnets-rename-";

    private sealed record CaseStep(string Old, string Passing, string New, string Hash);

    /// <summary>
    /// A rename that only changes letter case: Finder or Explorer renaming "photo.jpg" to "Photo.jpg", or a folder "trip" to "Trip".
    /// The nest never holds two names that differ only in case (computers on Windows and macOS could not), so
    /// <see cref="SyncStore.MoveAsync"/> refuses it in one step. As the apps' own rename does (NestFiles), it goes in two ordinary
    /// moves with the base check: every file to a passing name beside the old one, then to the new name. When a step fails, what
    /// was moved is moved back. Computers see the old name deleted and the new one added; history keeps the old version.
    /// </summary>
    private static async Task<IResult> RenameCaseAsync(Dav dav, string from, string to, List<ManifestEntry> sources, bool folder, IHubContext<SyncHub> hub)
    {
        if (!string.Equals(PathRules.Parent(from), PathRules.Parent(to), StringComparison.Ordinal))
            return Text(StatusCodes.Status409Conflict, "Only the name itself can change its letter case here. Rename the folder above on its own.");
        var passing = from + PassingMark + Guid.NewGuid().ToString("N")[..8];
        var steps = sources.Select(s => new CaseStep(s.Path, passing + s.Path[from.Length..], to + s.Path[from.Length..], s.Hash!)).ToList();
        if (steps.Any(s => !PathRules.IsValid(s.Passing) || !PathRules.IsValid(s.New) || !dav.MayTouch(s.Passing) || !dav.MayTouch(s.New)))
            return Text(StatusCodes.Status403Forbidden, "That can't be renamed here; rename it in the Pairnets app.");

        var parked = new List<CaseStep>(); // under the passing name now
        foreach (var step in steps)
        {
            var result = await MoveOneAsync(dav, step.Old, step.Passing, step.Hash);
            if (result.Status != ChangeStatus.Ok)
                return await UndoCaseRenameAsync(dav, hub, parked, [], result);
            parked.Add(step);
        }
        var renamed = new List<CaseStep>();
        foreach (var step in steps)
        {
            var result = await MoveOneAsync(dav, step.Passing, step.New, step.Hash);
            if (result.Status != ChangeStatus.Ok)
                return await UndoCaseRenameAsync(dav, hub, parked, renamed, result);
            parked.Remove(step);
            renamed.Add(step);
        }
        foreach (var step in steps)
        {
            await Endpoints.BroadcastAsync(dav.Http, hub, step.Old);
            await Endpoints.BroadcastAsync(dav.Http, hub, step.New);
        }
        if (folder)
        {
            dav.Folders.Remove(from);
            dav.Folders.Add(to);
        }
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    /// <summary>
    /// Puts back what a failed rename by letter case moved, and answers why it failed: renamed files go to their passing name
    /// first (the old and the new name can't be there both), then every file to its old name, each as it is now. A file that
    /// can't go back stays whole under the name it has (and is logged); every step kept the version it replaced in history.
    /// </summary>
    private static async Task<IResult> UndoCaseRenameAsync(Dav dav, IHubContext<SyncHub> hub, List<CaseStep> parked, List<CaseStep> renamed, ChangeResult failure)
    {
        var stuck = new List<string>();
        foreach (var step in renamed)
        {
            if (await MoveBackAsync(dav, step.New, step.Passing))
                parked.Add(step);
            else
                stuck.Add(step.New);
        }
        foreach (var step in parked)
        {
            if (!await MoveBackAsync(dav, step.Passing, step.Old))
                stuck.Add(step.Passing);
        }
        foreach (var path in parked.Select(s => s.Old).Concat(stuck))
            await Endpoints.BroadcastAsync(dav.Http, hub, path);
        var message = failure.Status == ChangeStatus.Conflict ? "The file changed on the nest in the meantime." : failure.Message ?? "The nest refused this.";
        if (stuck.Count > 0)
        {
            dav.Http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DavEndpoints))
                .LogWarning("A rename by letter case failed ({Reason}) and could not be undone for {Paths}", message, string.Join(", ", stuck));
            message += $" Some files kept a passing name ending in {PassingMark}…; rename them in the Pairnets app.";
        }
        return Text(failure.Status == ChangeStatus.Conflict ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status409Conflict, message);
    }

    private static async Task<bool> MoveBackAsync(Dav dav, string from, string to) =>
        dav.Store.Manifest.Get(from) is { Deleted: false, Hash: { } now } && (await MoveOneAsync(dav, from, to, now)).Status == ChangeStatus.Ok;

    /// <summary>
    /// A held empty file renamed (Explorer's "New Text Document.txt" getting its name): it stays held under the new name. Onto a
    /// stored file it is a real change, like an empty PUT over it: that file becomes empty and its old version goes to history.
    /// </summary>
    private static async Task<IResult> MoveHeldAsync(Dav dav, string from, string to, bool overwrite, IHubContext<SyncHub> hub)
    {
        if (dav.Visible().Any(e => e.Path.StartsWith(to + "/", StringComparison.Ordinal)) || dav.HasHeldInside(to))
            return Text(StatusCodes.Status409Conflict, "A folder with that name is in the way.");
        foreach (var parent in PathRules.DirectoryPrefixes(to))
        {
            if (dav.Store.Manifest.Get(parent) is { Deleted: false } || dav.HeldEmpty(parent) is not null)
                return Text(StatusCodes.Status409Conflict, "A file with the name of the folder is in the way.");
        }
        var target = dav.Store.Manifest.Get(to) is { Deleted: false } live ? live : null;
        var targetHeld = target is null && dav.HeldEmpty(to) is not null;
        if ((target is not null || targetHeld) && !overwrite)
            return Text(StatusCodes.Status412PreconditionFailed, "There is already something with that name.");
        if (target is null)
        {
            dav.EmptyFiles.Move(from, to);
            return Results.StatusCode(targetHeld ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
        }
        var result = await dav.Store.PutAsync(to, target.Hash!, null, Stream.Null, CancellationToken.None);
        switch (result.Status)
        {
            case ChangeStatus.Ok or ChangeStatus.Unchanged:
                if (result.Status == ChangeStatus.Ok)
                    await Endpoints.BroadcastAsync(dav.Http, hub, to);
                dav.EmptyFiles.Remove(from);
                return Results.NoContent();
            case ChangeStatus.Conflict:
                return Text(StatusCodes.Status412PreconditionFailed, "The file changed on the nest in the meantime.");
            default:
                return Text(StatusCodes.Status409Conflict, result.Message ?? "The nest refused this.");
        }
    }

    /// <summary>
    /// The nest path in a <c>Destination</c> header: a full address or just a path, under the same base the client connected to.
    /// Anything else (another host, another base, "..") is refused, so a client cannot name a place outside the nest's files.
    /// </summary>
    private static bool TryDestination(Dav dav, out string to)
    {
        to = string.Empty;
        var header = dav.Http.Request.Headers["Destination"].ToString().Trim();
        if (header.Length == 0 || header.Length > 2048)
            return false;
        string path;
        if (Uri.TryCreate(header, UriKind.Absolute, out var absolute))
        {
            // On the nest itself the host must be its own ("localhost", 127.0.0.1 and ::1 count as one); behind the service it is
            // the public name, which the service vouches for.
            if (dav.BasePath == "/dav" && !SameHost(absolute.Host, dav.Http.Request.Host.Host))
                return false;
            path = absolute.AbsolutePath;
        }
        else if (header.StartsWith('/'))
        {
            path = header.Split('?', '#')[0];
        }
        else
        {
            return false;
        }
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return false;
        }
        var prefix = dav.BasePath + "/";
        if (!decoded.StartsWith(prefix, StringComparison.Ordinal) && decoded != dav.BasePath)
            return false;
        var rest = decoded.Length > prefix.Length ? decoded[prefix.Length..].Trim('/') : string.Empty;
        if (rest.Split('/').Any(s => s is "." or ".." || (s.Length == 0 && rest.Length > 0)))
            return false;
        to = rest;
        return true;
    }

    // ------------------------------------------------------------------ small helpers

    private static bool SameHost(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || IsLoopback(a) && IsLoopback(b);

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip);

    private static IResult Text(int status, string message) => Results.Text(message, "text/plain", Encoding.UTF8, status);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), PairnetsJson.Options, statusCode: status);
}
