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
/// list. Its own computer it may rename, give an icon or remove ("Leave").</item>
/// <item>On an encrypted nest it may only have the whole nest (the paths are ids there); one with folders gets
/// <c>not_shared</c> for everything but what it needs to find that out and leave.</item>
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
        if (access.Folders is not null && SharedPathOf(ctx) is { } target && !(IsValidPath(target) && access.Allows(target.Path)))
            return Refuse(NotShared, "This file is not in the folders shared with you.");
        return null;
    }

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
            || path.StartsWithSegments("/api/keys/seal", StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsPut(method) && path.StartsWithSegments("/api/keys/recovery", StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsDelete(method) && path.StartsWithSegments("/api/keys/envelopes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Everything that changes files: PUT and DELETE /api/file, every /api/upload call, restoring a version.</summary>
    private static bool IsWrite(PathString path, string method) =>
        (path.StartsWithSegments("/api/file", StringComparison.OrdinalIgnoreCase) && (HttpMethods.IsPut(method) || HttpMethods.IsDelete(method)))
        || path.StartsWithSegments("/api/upload", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/history/restore", StringComparison.OrdinalIgnoreCase);

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
