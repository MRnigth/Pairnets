using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// The Pairnets service's calls to a nest linked to an account (cloud/RELAY.md §4): how the server is doing, its
/// computers, and adding or removing one when the owner does so on their account page or an app signs in. Only the
/// service can reach a linked nest, but the relay also carries the apps' requests, so every call under /api/relay is
/// signed with the nest key (<see cref="RelaySignature"/>, checked by <see cref="RelayAuthMiddleware"/>). The relay
/// itself refuses to pass /api/relay/* from anyone. On a nest that is not linked every call here answers 401.
/// <para>
/// Friend invites (1.0.61.2): the status lists the <see cref="FriendsFeature"/>, a computer can be added for a friend
/// (with what it may do and whose it is), a friend's share is changed or ended for all their computers at once
/// (/people/{personId}), and the service hands over who waits for the owner's OK (/requests).
/// </para>
/// </summary>
public static class RelayEndpoints
{
    public const string Prefix = "/api/relay";

    /// <summary>In the status' features: this nest knows friends (the service only adds a friend's computer to such a nest).</summary>
    public const string FriendsFeature = "friends";

    public static readonly IReadOnlyList<string> Features = [FriendsFeature];

    public sealed record Status(string? ServerVersion, int Devices, long? FreeBytes, long DataBytes, string? Name, IReadOnlyList<string> Features, bool Sealed);

    /// <param name="Access">"owner", "edit" or "view".</param>
    /// <param name="Folders">The shared folders of a friend's computer; null for the whole nest.</param>
    public sealed record Device(string Id, string Name, string? System, string CreatedAt, string? LastSeen, string Access, IReadOnlyList<string>? Folders,
        string? Person, string? PersonId);

    public sealed record DeviceList(IReadOnlyList<Device> Devices);

    /// <summary>
    /// The body of <c>POST /devices</c>. "access" and "folders" are read on their own (see <see cref="DeviceAccess.TryRead"/>):
    /// without them the computer is the owner's, as before friends.
    /// </summary>
    public sealed record AddDeviceRequest(string? Name, string? System, string? ApprovedBy, string? Person = null, string? PersonId = null);

    /// <summary>How many computers a change to a friend's share touched (0 is fine: it is done already).</summary>
    public sealed record PeopleResult(int Devices);

    public sealed record RequestsResult(int Requests);

    public sealed record AddedDevice(string Id, string Name, string Key);

    public sealed record RemoveResult(bool Removed);

    public sealed record NameRequest(string? Name);

    public sealed record RenamedDevice(string Id, string Name);

    public sealed record RelayError(string Error);

    public static void Map(WebApplication app)
    {
        // The middleware already checked every path under /api/relay; this makes sure no endpoint here can answer a
        // call it did not check, whatever the path looked like on the way in.
        var relay = app.MapGroup(Prefix).AddEndpointFilter(async (context, next) =>
            RelayAuthMiddleware.VerifiedBody(context.HttpContext) is null ? BadSignature() : await next(context));

        relay.MapGet("/status", (SyncStore store, DeviceKeys keys) =>
        {
            var (free, _) = ServerUpdater.DiskSpace(store.Paths.DataDir);
            return Json(new Status(PairnetsInfo.ProductVersion, keys.Store.ListDevices().Count, free, store.Manifest.LiveBytes(), keys.Store.NestName,
                Features, store.Sealed));
        });

        // The nest's name as the owner gave it on the account page; kept so the apps can show it (/api/info).
        relay.MapPut("/name", async (HttpContext ctx, DeviceKeys keys, IHubContext<SyncHub> hub) =>
        {
            NameRequest? body;
            try
            {
                body = JsonSerializer.Deserialize<NameRequest>(RelayAuthMiddleware.VerifiedBody(ctx)!, PairnetsJson.Options);
            }
            catch (JsonException)
            {
                body = null;
            }
            var name = PlainText.Clean(body?.Name, AuthStore.MaxNameLength);
            if (name.Length == 0)
                return Error(StatusCodes.Status400BadRequest, "bad_name");
            if (!string.Equals(keys.Store.NestName, name, StringComparison.Ordinal))
            {
                keys.Store.NestName = name;
                try
                {
                    await hub.Clients.All.SendAsync(SyncHub.NestRenamedMethod, name, CancellationToken.None);
                }
                catch (Exception)
                {
                    // The apps also read it every few minutes (/api/info).
                }
            }
            return Json(new { name });
        });

        relay.MapGet("/devices", (DeviceKeys keys, DeviceRegistry devices) =>
        {
            // When each computer last asked something (only computers that ever did are in the registry).
            var lastSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var seen in devices.List())
            {
                if (seen.Id is { } id)
                    lastSeen.TryAdd(id, seen.LastSeen);
            }
            return Json(new DeviceList(keys.Store.ListDevices()
                .Select(d => new Device(d.Id, d.Name, d.System, Iso(d.Created), lastSeen.TryGetValue(d.Id, out var at) ? Iso(at) : null,
                    d.AccessOrOwner.Level, d.AccessOrOwner.Folders, d.Person, d.PersonId))
                .ToList()));
        });

        // An app signed in with the owner's account, or a friend's the owner let in: the service asks for its key and hands
        // it over once. A friend's computer comes with what it may do and whose it is; an encrypted nest is only shared whole.
        relay.MapPost("/devices", (HttpContext ctx, AuthStore auth, DeviceRegistry devices, SyncStore store, SyncOptions options, ILogger<AddDeviceRequest> log) =>
        {
            AddDeviceRequest? body;
            try
            {
                body = JsonSerializer.Deserialize<AddDeviceRequest>(RelayAuthMiddleware.VerifiedBody(ctx)!, PairnetsJson.Options);
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body is null)
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            if (BodyJson(ctx) is not { } json || !DeviceAccess.TryRead(json, ownerAllowed: true, out var access)
                || (body.PersonId is { } given && !DeviceAccess.IsPersonId(given)) || (!access.IsOwner && body.PersonId is null))
                return Error(StatusCodes.Status400BadRequest, "bad_access");
            if (access.Folders is not null && store.Sealed)
                return Error(StatusCodes.Status400BadRequest, "sealed_whole_only");
            var who = Clean(body.ApprovedBy, 80);
            var approvedBy = who is null ? $"approved on {options.RelayServiceHost}" : $"approved by {who} on {options.RelayServiceHost}";
            // Exactly like Allow on the nest's own website: a new key, only its hash kept, the name made unique.
            var (device, key) = auth.AddDevice(body.Name ?? string.Empty, body.System, approvedBy, access,
                PlainText.Clean(body.Person, AuthStore.MaxPersonLength) is { Length: > 0 } person ? person : null, body.PersonId);
            if (access.IsOwner)
            {
                // Only the owner's own computer takes over the history of one that used the shared token under its name.
                devices.Adopt(device.Name, new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey).RegistryKey, device.Name);
                log.LogInformation("{Name} joined the nest ({By})", device.Name, device.ApprovedBy);
            }
            else
            {
                log.LogInformation("{Name} joined the nest as a friend's computer ({Access}, {Share}; {By})", device.Name, access.Level,
                    access.Folders is null ? "whole nest" : $"{access.Folders.Count} folder(s)", device.ApprovedBy);
            }
            return Json(new AddedDevice(device.Id, device.Name, key));
        });

        // Renamed on the account page: the same as renaming it in the apps (made unique, the other apps told at once).
        // Answers the name the nest settled on ("LAPTOP (2)" when another computer already has the name).
        relay.MapPatch("/devices/{id}", async (string id, HttpContext ctx, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub,
            ILogger<DeviceKeys> log) =>
        {
            NameRequest? body;
            try
            {
                body = JsonSerializer.Deserialize<NameRequest>(RelayAuthMiddleware.VerifiedBody(ctx)!, PairnetsJson.Options);
            }
            catch (JsonException)
            {
                body = null;
            }
            if (AuthStore.CleanName(body?.Name) is not { } name)
                return Error(StatusCodes.Status400BadRequest, "bad_name");
            if (keys.Store.GetDevice(id) is not { IsActive: true })
                return Error(StatusCodes.Status404NotFound, "not_found");
            var renamed = await Endpoints.RenameDeviceAsync(id, name, keys, devices, hub);
            if (renamed is null)
                return Error(StatusCodes.Status404NotFound, "not_found");
            log.LogInformation("A computer was renamed to {Name} on the account page", renamed.Name);
            return Json(new RenamedDevice(renamed.Id, renamed.Name));
        });

        // Removed on the account page: the same as removing it in the apps (key, push connections, the other apps told).
        relay.MapDelete("/devices/{id}", async (string id, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub, SyncOptions options,
            ILogger<DeviceKeys> log) =>
        {
            if (keys.Store.GetDevice(id) is not { IsActive: true } device)
                return Error(StatusCodes.Status404NotFound, "not_found");
            await Endpoints.RemoveDeviceAsync(device, $"removed on {options.RelayServiceHost}", keys, devices, hub, log);
            return Json(new RemoveResult(true));
        });

        // The owner changed what a friend may do on the account page: every computer of that person, at once (keys looked up
        // again, live push connections told what they may hear). The owner's own computers are never changed.
        relay.MapPatch("/people/{personId}", (string personId, HttpContext ctx, DeviceKeys keys, SyncStore store, FriendConnections friends,
            ILogger<DeviceKeys> log) =>
        {
            if (BodyJson(ctx) is not { } json)
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            if (!DeviceAccess.TryRead(json, ownerAllowed: false, out var access))
                return Error(StatusCodes.Status400BadRequest, "bad_access");
            if (access.Folders is not null && store.Sealed)
                return Error(StatusCodes.Status400BadRequest, "sealed_whole_only");
            if (!DeviceAccess.IsPersonId(personId))
                return Json(new PeopleResult(0)); // no computer can belong to such an id
            var changed = keys.Store.SetFriendAccess(personId, access);
            foreach (var device in changed)
            {
                keys.Forget(device.Id);
                friends.Update(device.Id, access);
            }
            if (changed.Count > 0)
                log.LogInformation("A friend's share was changed on the account page ({Access}, {Share}; {Count} computer(s))", access.Level,
                    access.Folders is null ? "whole nest" : $"{access.Folders.Count} folder(s)", changed.Count);
            return Json(new PeopleResult(changed.Count));
        });

        // The owner stopped sharing with a friend (or the friend left): every computer of that person is removed, the same
        // way as removing one computer (keys refused, the apps told, live connections closed).
        relay.MapDelete("/people/{personId}", async (string personId, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub, SyncOptions options,
            ILogger<DeviceKeys> log) =>
        {
            IReadOnlyList<PairedDevice> gone = DeviceAccess.IsPersonId(personId) ? keys.Store.ListFriendDevices(personId) : [];
            foreach (var device in gone)
                await Endpoints.RemoveDeviceAsync(device, $"sharing ended on {options.RelayServiceHost}", keys, devices, hub, log);
            return Json(new PeopleResult(gone.Count));
        });

        // Who asked to join and waits for the owner's OK: the service sends the whole list each time it changes.
        relay.MapPut("/requests", (HttpContext ctx, FriendRequests requests, ILogger<FriendRequests> log) =>
        {
            if (BodyJson(ctx) is not { } json)
                return Error(StatusCodes.Status400BadRequest, "bad_request");
            if (FriendRequests.TryRead(json, out var list) is { } problem)
                return Error(StatusCodes.Status400BadRequest, problem);
            requests.Replace(list);
            log.LogInformation("{Count} person(s) wait to be let in", list.Count);
            return Json(new RequestsResult(list.Count));
        });
    }

    /// <summary>The signed body as JSON, or null when it is not JSON.</summary>
    private static JsonElement? BodyJson(HttpContext ctx)
    {
        try
        {
            using var doc = JsonDocument.Parse(RelayAuthMiddleware.VerifiedBody(ctx)!);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The one answer to any call that is not properly signed: no detail about which check failed.</summary>
    public static IResult BadSignature() => Error(StatusCodes.Status401Unauthorized, "bad_signature");

    public static IResult Error(int status, string error) => Results.Json(new RelayError(error), PairnetsJson.Options, statusCode: status);

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    /// <summary>ISO 8601 in UTC with whole seconds, as the service's JSON uses (2026-10-09T00:00:00Z).</summary>
    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Clean(string? text, int max)
    {
        if (text is null)
            return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > max)
            clean = clean[..max].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }
}

/// <summary>
/// Checks the signature of every request under /api/relay (<see cref="RelaySignature"/>) before anything else sees it.
/// The body (at most 4 KiB) is read here, because the signature covers its exact bytes; the endpoints read it from
/// <see cref="VerifiedBody"/>. A failed check is logged with its reason, never with a header's value.
/// </summary>
public sealed class RelayAuthMiddleware(RequestDelegate next, RelaySignature signature, ILogger<RelayAuthMiddleware> log)
{
    public const int MaxBodyBytes = 4096;

    /// <summary>
    /// The calls that carry folder lists may be longer: the people waiting (20 people, each with 20 folder names of up to
    /// 255 characters, up to 3 bytes each in UTF-8, is about 310 KiB), a friend's folders, and a friend's computer added.
    /// </summary>
    public const int MaxListBodyBytes = 512 * 1024;

    private const string BodyItem = "pairnets.relay-body";

    public static bool IsRelayPath(PathString path) => path.StartsWithSegments(RelayEndpoints.Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>How long a body may be: <see cref="MaxListBodyBytes"/> for /requests, /people/… and /devices, else <see cref="MaxBodyBytes"/>.</summary>
    public static int MaxBodyFor(PathString path) =>
        path.StartsWithSegments(RelayEndpoints.Prefix + "/requests", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(RelayEndpoints.Prefix + "/people", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(RelayEndpoints.Prefix + "/devices", StringComparison.OrdinalIgnoreCase)
            ? MaxListBodyBytes
            : MaxBodyBytes;

    /// <summary>The body of a request whose signature checked out, or null.</summary>
    public static byte[]? VerifiedBody(HttpContext context) => context.Items[BodyItem] as byte[];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsRelayPath(context.Request.Path))
        {
            await next(context);
            return;
        }
        var request = context.Request;
        var max = MaxBodyFor(request.Path);
        var body = request.ContentLength > max ? null : await ReadBodyAsync(request.Body, max, context.RequestAborted);
        if (body is null)
        {
            await RelayEndpoints.Error(StatusCodes.Status413PayloadTooLarge, "too_large").ExecuteAsync(context);
            return;
        }
        var headers = request.Headers;
        var outcome = signature.Check(headers[RelaySignature.NestHeader].ToString(), headers[RelaySignature.TimeHeader].ToString(),
            headers[RelaySignature.NonceHeader].ToString(), headers[RelaySignature.SignatureHeader].ToString(),
            request.Method, PathAndQuery(context), body);
        if (outcome != RelaySignature.Outcome.Ok)
        {
            log.LogWarning("Refused a call to {Method} {Path} from {Address}: {Reason}", request.Method, request.Path.Value,
                context.Connection.RemoteIpAddress, outcome);
            await RelayEndpoints.BadSignature().ExecuteAsync(context);
            return;
        }
        context.Items[BodyItem] = body;
        await next(context);
    }

    /// <summary>The path and query exactly as they arrived (still escaped), which is what the service signed.</summary>
    private static string PathAndQuery(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw) && raw[0] == '/')
            return raw;
        return (context.Request.PathBase + context.Request.Path).ToUriComponent() + context.Request.QueryString.ToUriComponent();
    }

    /// <summary>The whole body, or null when it is longer than <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream body, int max, CancellationToken ct)
    {
        var buffer = new byte[max + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await body.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                break;
            read += n;
        }
        return read > max ? null : buffer[..read];
    }
}
