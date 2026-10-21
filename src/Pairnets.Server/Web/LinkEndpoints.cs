using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// Linked nests on this nest (docs/DECISIONS.md, "4 nests per account, and linked nests"):
/// <list type="bullet">
/// <item>The Pairnets service sets a link up, changes it and ends it under /api/relay/links. Those calls are signed with
/// the nest key, like the rest of /api/relay (<see cref="RelayAuthMiddleware"/>).</item>
/// <item>The apps read the links' state at GET /api/links, to show "Linked from Oskar" and "waiting" on the folder.</item>
/// </list>
/// The other side of a link needs nothing here: there the joining nest is one more member key.
/// </summary>
public static partial class LinkEndpoints
{
    public sealed record AddLinkRequest(string? Id, string? RemoteNestId, string? DeviceId, string? Key, string? Folder, string? Access,
        IReadOnlyList<string>? Folders, string? OwnerName, string? NestName);

    public sealed record ChangeLinkRequest(string? Access, IReadOnlyList<string>? Folders);

    /// <summary>A link as the service sees it; never the key.</summary>
    public sealed record LinkView(string Id, string RemoteNestId, string Folder, string Access, IReadOnlyList<string>? Folders, string OwnerName,
        string NestName, string State, string? LastInStep, string? LastError);

    /// <param name="LinkedIn">This nest's top-level folders that came in through a link: the service never lets this nest share them on.</param>
    public sealed record LinkList(IReadOnlyList<LinkView> Links, IReadOnlyList<string> LinkedIn);

    /// <summary>A link as the apps show it on its folder.</summary>
    public sealed record AppLink(string Folder, string OwnerName, string NestName, string Access, string State, string? LastInStep, string? Message);

    public sealed record AppLinkList(IReadOnlyList<AppLink> Links);

    public static void Map(WebApplication app)
    {
        var relay = app.MapGroup(RelayEndpoints.Prefix + "/links").AddEndpointFilter(async (context, next) =>
            RelayAuthMiddleware.VerifiedBody(context.HttpContext) is null ? RelayEndpoints.BadSignature() : await next(context));

        relay.MapGet("", (LinkStore links) => Json(new LinkList(links.List().Select(View).ToList(), links.LinkedInFolders())));

        relay.MapPost("", (HttpContext ctx, LinkStore links, SyncStore store, NestLinkWorker worker, ILogger<LinkStore> log) =>
        {
            var body = Read<AddLinkRequest>(ctx);
            if (body is null || !LinkIdPattern().IsMatch(body.Id ?? "") || !Relay.IsNestId(body.RemoteNestId) || string.IsNullOrWhiteSpace(body.DeviceId)
                || body.Key is not { Length: > 3 } key || !key.StartsWith(AuthStore.KeyPrefix, StringComparison.Ordinal)
                || ParseAccess(body.Access) is not { } access || CleanFolders(body.Folders, out var folders) is false
                || RelayEndpoints.Clean(body.OwnerName, 40) is not { } owner || RelayEndpoints.Clean(body.NestName, 64) is not { } nestName)
                return RelayEndpoints.Error(StatusCodes.Status400BadRequest, "bad_request");
            if (store.Sealed)
                return RelayEndpoints.Error(StatusCodes.Status409Conflict, "sealed");
            if (FolderFor(body.Folder, owner, nestName, body.Id!, links, store) is not { } folder)
                return RelayEndpoints.Error(StatusCodes.Status400BadRequest, "bad_folder");
            var link = links.Add(body.Id!, body.RemoteNestId!, body.DeviceId!.Trim(), key, folder, access, folders, owner, nestName);
            if (link is null)
                return RelayEndpoints.Error(StatusCodes.Status409Conflict, "link_limit");
            log.LogInformation("Linked with {Owner}'s {Nest} into {Folder}", owner, nestName, folder);
            worker.Kick();
            return Json(View(link));
        });

        relay.MapPatch("/{id}", (string id, HttpContext ctx, LinkStore links, NestLinkWorker worker) =>
        {
            var body = Read<ChangeLinkRequest>(ctx);
            if (body is null || ParseAccess(body.Access) is not { } access || CleanFolders(body.Folders, out var folders) is false)
                return RelayEndpoints.Error(StatusCodes.Status400BadRequest, "bad_request");
            if (links.Get(id) is not { IsActive: true } || !links.Change(id, access, folders))
                return RelayEndpoints.Error(StatusCodes.Status404NotFound, "not_found");
            worker.Kick();
            return Json(View(links.Get(id)!));
        });

        relay.MapDelete("/{id}", (string id, LinkStore links, ILogger<LinkStore> log) =>
        {
            if (links.Get(id) is not { } link)
                return RelayEndpoints.Error(StatusCodes.Status404NotFound, "not_found");
            links.Unlink(id);
            log.LogInformation("The link with {Owner}'s {Nest} was ended; {Folder} stays", link.OwnerName, link.NestName, link.Folder);
            return Json(new RelayEndpoints.RemoveResult(true));
        });

        // Any computer of this nest: the linked folders, so the apps can mark them.
        app.MapGet("/api/links", (HttpContext ctx, LinkStore links) => DeviceIdentity.Of(ctx) is null
            ? Results.Json(new ErrorBody(ErrorCodes.Unauthorized, "Not signed in."), PairnetsJson.Options, statusCode: StatusCodes.Status401Unauthorized)
            : Json(new AppLinkList(links.List().Select(l => new AppLink(l.Folder, l.OwnerName, l.NestName, l.Access == LinkAccess.Edit ? "edit" : "view",
                StateName(l.State), Iso(l.LastInStep), l.State is LinkState.InStep or LinkState.New ? null : l.LastError)).ToList())));
    }

    private static LinkView View(NestLink l) =>
        new(l.Id, l.RemoteNestId, l.Folder, l.Access == LinkAccess.Edit ? "edit" : "view", l.Folders, l.OwnerName, l.NestName, StateName(l.State),
            Iso(l.LastInStep), l.LastError);

    /// <summary>"new", "in_step", "waiting", "problem", "unlinked".</summary>
    public static string StateName(LinkState state) => state switch
    {
        LinkState.InStep => "in_step",
        LinkState.Waiting => "waiting",
        LinkState.Problem => "problem",
        LinkState.Unlinked => "unlinked",
        _ => "new",
    };

    private static LinkAccess? ParseAccess(string? access) => access switch
    {
        null or "view" => LinkAccess.View,
        "edit" => LinkAccess.Edit,
        _ => null,
    };

    /// <summary>Null = the whole nest. False when a name is not one top-level folder name.</summary>
    private static bool CleanFolders(IReadOnlyList<string>? given, out IReadOnlyList<string>? folders)
    {
        folders = null;
        if (given is null)
            return true;
        if (given.Count is 0 or > 100 || given.Any(f => f.Contains('/') || PathRules.Check(f) != PathProblem.None))
            return false;
        folders = given.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    /// <summary>
    /// The link's folder here: the one the service names, else "From Oskar – Home nest". A folder this nest already has
    /// for something else gets " (2)" and so on. The same link set up again keeps its folder.
    /// </summary>
    private static string? FolderFor(string? asked, string owner, string nestName, string id, LinkStore links, SyncStore store)
    {
        if (links.Get(id) is { } again)
            return again.Folder;
        var stem = RelayEndpoints.Clean(asked, 120) ?? $"From {owner} – {nestName}";
        stem = stem.Replace('/', '-').Replace('\\', '-').Trim().TrimEnd('.');
        if (PathRules.Check(stem) != PathProblem.None)
            return null;
        var used = store.ReadManifest(null).Entries.Where(e => !e.Deleted).Select(e => e.Path.Split('/')[0])
            .Concat(links.LinkedInFolders()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folder = stem;
        for (var n = 2; used.Contains(folder); n++)
            folder = $"{stem} ({n})";
        return folder;
    }

    private static T? Read<T>(HttpContext ctx) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(RelayAuthMiddleware.VerifiedBody(ctx)!, PairnetsJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    private static string? Iso(DateTimeOffset? time) =>
        time?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex("^lnk_[a-z0-9]{8,40}$")]
    private static partial Regex LinkIdPattern();
}
