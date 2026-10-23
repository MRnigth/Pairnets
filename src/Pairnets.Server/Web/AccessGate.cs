using Pairnets.Core;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// What a friend's computer may do (friend invites, 1.0.61.2), checked right after the sign-in for every request it
/// makes. The owner's computers, the shared token and the nest's website pass untouched. A friend's computer:
/// <list type="bullet">
/// <item>"View only" changes nothing: uploading, deleting and restoring get 403 <c>view_only</c>.</item>
/// <item>A file outside the shared folders gets 403 <c>not_shared</c>, by the path it names (an upload in pieces by the
/// path it was started for).</item>
/// <item>What only the owner does gets 403 <c>owner_only</c>: removing or renaming another computer, updating the
/// server, turning on encryption or replacing its recovery envelope, taking a computer's key envelope away, the People
/// list, nest downloads. Its own computer it may rename, give an icon or remove ("Leave").</item>
/// <item>On an encrypted nest it may only have the whole nest (the paths are ids there); one with folders gets
/// <c>not_shared</c> for everything but what it needs to find that out and leave.</item>
/// <item>A folder that came in through a linked nest (<see cref="LinkStore.LinkedInFolders"/>) is never shared on, even
/// with a friend who has the whole nest: that nest's owner never let them in. It gets <c>not_shared</c>, and listings
/// leave it out (<see cref="Sees"/>).</item>
/// </list>
/// Listings (the manifest, "Changed" on the push channel) are filtered where they are made: see
/// <see cref="Endpoints"/> and <see cref="FriendConnections"/>.
/// </summary>
public static class AccessGate
{
    public const string ViewOnly = "view_only";
    public const string NotShared = "not_shared";
    public const string OwnerOnly = "owner_only";

    /// <summary>The body of a refusal: the code under both names the apps and the service read.</summary>
    public sealed record Refusal(string Code, string Message, string Error);

    public static async Task InvokeAsync(HttpContext ctx, Func<Task> next)
    {
        if (DeviceIdentity.Of(ctx) is not { } me || me.AccessOrOwner is not { IsOwner: false } access)
        {
            await next();
            return;
        }
        if (Check(ctx, me, access) is { } refusal)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(refusal, PairnetsJson.Options);
            return;
        }
        await next();
    }

    /// <summary>Why a friend's computer may not make this request, or null when it may.</summary>
    internal static Refusal? Check(HttpContext ctx, DeviceIdentity me, DeviceAccess access)
    {
        var request = ctx.Request;
        var path = request.Path;
        var method = request.Method;

        if (IsOwnDevice(path, method, me) || (HttpMethods.IsGet(method) && (Is(path, "/api/access") || Is(path, "/api/me"))))
            return null; // Leave, and how it learns what it may do, always work
        if (access.Folders is not null && ctx.RequestServices.GetRequiredService<SyncStore>().Sealed)
            return Refuse(NotShared, "This nest is encrypted and can only be shared whole.");
        if (IsOwnerOnly(path, method))
            return Refuse(OwnerOnly, "Only the nest's owner can do this.");
        if (!access.CanWrite && IsWrite(path, method))
            return Refuse(ViewOnly, "This nest is shared with you to look at, not to change.");
        var linkedIn = ctx.RequestServices.GetRequiredService<LinkStore>().LinkedInFolders();
        foreach (var target in SharedPathsOf(ctx))
        {
            if (access.Folders is not null && !(IsValidPath(target) && access.Allows(target.Path)))
                return Refuse(NotShared, "This file is not in the folders shared with you.");
            if (LinkStore.IsUnder(target.Path, linkedIn))
                return Refuse(NotShared, "This folder came from another nest, so it isn't shared with you.");
        }
        return null;
    }

    /// <summary>
    /// What a computer may see of a path in listings and push messages: everything for the owner's; for a friend's, what
    /// is inside its share and not in a folder that came in through a link.
    /// </summary>
    public static bool Sees(DeviceAccess access, string path, IReadOnlyList<string> linkedIn) =>
        access.IsOwner || (access.Allows(path) && !LinkStore.IsUnder(path, linkedIn));

    private static bool Is(PathString path, string value) => string.Equals(path.Value?.TrimEnd('/'), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Renaming, re-iconing or removing the computer itself (its key's own id, or "me").</summary>
    private static bool IsOwnDevice(PathString path, string method, DeviceIdentity me)
    {
        if (!path.StartsWithSegments("/api/devices", StringComparison.OrdinalIgnoreCase, out var rest))
            return false;
        var segments = Segments(rest);
        if (segments is [var only] && only.Equals("me", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsPatch(method);
        return segments switch
        {
            [var id] => HttpMethods.IsDelete(method) && id == me.Id,
            [var id, var what] => HttpMethods.IsPatch(method) && what.Equals("icon", StringComparison.OrdinalIgnoreCase) && id == me.Id,
            _ => false,
        };
    }

    private static bool IsOwnerOnly(PathString path, string method)
    {
        if (path.StartsWithSegments("/api/devices", StringComparison.OrdinalIgnoreCase, out var rest))
        {
            // Another computer's removal (its own was let through above) or icon; the list itself is fine.
            var segments = Segments(rest);
            return (HttpMethods.IsDelete(method) && segments.Length == 1) || (segments.Length == 2 && segments[1].Equals("icon", StringComparison.OrdinalIgnoreCase));
        }
        return path.StartsWithSegments("/api/update", StringComparison.OrdinalIgnoreCase) // "Update server" and its diagnostics
            || path.StartsWithSegments("/api/people", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/storage", StringComparison.OrdinalIgnoreCase) // where the nest keeps its files, and moving them
            || path.StartsWithSegments("/api/purge", StringComparison.OrdinalIgnoreCase) // deleting for good, older versions too
            || path.StartsWithSegments("/api/links", StringComparison.OrdinalIgnoreCase) // the owner's linked folders
            || path.StartsWithSegments("/api/downloads", StringComparison.OrdinalIgnoreCase) // links the nest downloads by itself
            || path.StartsWithSegments("/api/offers", StringComparison.OrdinalIgnoreCase) // nests shared with the owner's account, and by whom
            || path.StartsWithSegments("/api/keys/seal", StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsPut(method) && path.StartsWithSegments("/api/keys/recovery", StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsDelete(method) && path.StartsWithSegments("/api/keys/envelopes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Everything that changes files: PUT and DELETE /api/file, every /api/upload call, moving, restoring a version, a nest download.</summary>
    private static bool IsWrite(PathString path, string method) =>
        (path.StartsWithSegments("/api/file", StringComparison.OrdinalIgnoreCase) && (HttpMethods.IsPut(method) || HttpMethods.IsDelete(method)))
        || path.StartsWithSegments("/api/upload", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/move", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/history/restore", StringComparison.OrdinalIgnoreCase)
        || (path.StartsWithSegments("/api/downloads", StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(method));

    /// <summary>The files a request is about: both ends of a move, else the one <see cref="SharedPathOf"/> names.</summary>
    private static IEnumerable<SharedPath> SharedPathsOf(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/api/move", StringComparison.OrdinalIgnoreCase))
            return [new SharedPath(ctx.Request.Query["from"].ToString()), new SharedPath(ctx.Request.Query["to"].ToString())];
        return SharedPathOf(ctx) is { } one ? [one] : [];
    }

    /// <summary>The file a request is about, or null when it names none (or an upload this nest does not know: that answers 404 anyway).</summary>
    private static SharedPath? SharedPathOf(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api/file", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/api/history", StringComparison.OrdinalIgnoreCase))
            return new SharedPath(ctx.Request.Query["path"].ToString());
        if (!path.StartsWithSegments("/api/upload", StringComparison.OrdinalIgnoreCase, out var rest))
            return null;
        if (Segments(rest) is not [var id, ..])
            return new SharedPath(ctx.Request.Query["path"].ToString()); // POST /api/upload?path=… starts one
        return ctx.RequestServices.GetRequiredService<UploadSessions>().PathOf(id) is { } started ? new SharedPath(started) : null;
    }

    private sealed record SharedPath(string Path);

    /// <summary>Only a path the store itself would take can be inside a share (no "..", no backslashes, no odd names).</summary>
    private static bool IsValidPath(SharedPath target) => Pairnets.Core.Paths.PathRules.IsValid(target.Path);

    private static string[] Segments(PathString rest) =>
        (rest.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

    private static Refusal Refuse(string code, string message) => new(code, message, code);
}
