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
/// </summary>
public static class RelayEndpoints
{
    public const string Prefix = "/api/relay";

    public sealed record Status(string? ServerVersion, int Devices, long? FreeBytes, long DataBytes);

    public sealed record Device(string Id, string Name, string? System, string CreatedAt, string? LastSeen);

    public sealed record DeviceList(IReadOnlyList<Device> Devices);

    public sealed record AddDeviceRequest(string? Name, string? System, string? ApprovedBy);

    public sealed record AddedDevice(string Id, string Name, string Key);

    public sealed record RemoveResult(bool Removed);

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
            return Json(new Status(PairnetsInfo.ProductVersion, keys.Store.ListDevices().Count, free, store.Manifest.LiveBytes()));
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
                .Select(d => new Device(d.Id, d.Name, d.System, Iso(d.Created), lastSeen.TryGetValue(d.Id, out var at) ? Iso(at) : null))
                .ToList()));
        });

        // An app signed in with the owner's account: the service asks for its key and hands it over once.
        relay.MapPost("/devices", (HttpContext ctx, AuthStore auth, DeviceRegistry devices, SyncOptions options, ILogger<AddDeviceRequest> log) =>
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
            var who = Clean(body.ApprovedBy, 80);
            var approvedBy = who is null ? $"approved on {options.RelayServiceHost}" : $"approved by {who} on {options.RelayServiceHost}";
            // Exactly like Allow on the nest's own website: a new key, only its hash kept, the name made unique.
            var (device, key) = auth.AddDevice(body.Name ?? string.Empty, body.System, approvedBy);
            devices.Adopt(device.Name, new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey).RegistryKey, device.Name);
            log.LogInformation("{Name} joined the nest ({By})", device.Name, device.ApprovedBy);
            return Json(new AddedDevice(device.Id, device.Name, key));
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

    private const string BodyItem = "pairnets.relay-body";

    public static bool IsRelayPath(PathString path) => path.StartsWithSegments(RelayEndpoints.Prefix, StringComparison.OrdinalIgnoreCase);

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
        var body = request.ContentLength > MaxBodyBytes ? null : await ReadBodyAsync(request.Body, MaxBodyBytes, context.RequestAborted);
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
