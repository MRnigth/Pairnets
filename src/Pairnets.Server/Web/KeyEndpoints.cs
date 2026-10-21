using Microsoft.AspNetCore.SignalR;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// An encrypted nest's key mailbox (docs/ENCRYPTION.md §2.1, §2.6). The nest stores sealed rings and passes unlock
/// messages between computers; it can read none of them. Signing in still only lets a computer talk to the nest:
/// opening any of this needs one of the user's computers or the recovery key.
/// </summary>
public static class KeyEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/keys", (SyncStore store, KeyStore keys) =>
            Json(new KeysInfo(store.Sealed, keys.HasRecovery, keys.Computers())));

        app.MapGet("/api/keys/envelopes/{id}", (string id, KeyStore keys) =>
            keys.GetEnvelope(id) is { } envelope
                ? Results.Bytes(envelope, "application/octet-stream")
                : Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No key envelope for that computer."));

        app.MapPut("/api/keys/envelopes/{id}", async (string id, HttpContext ctx, KeyStore keys) =>
            await ReadBodyAsync(ctx, KeyStore.MaxEnvelopeBytes) is { } body && keys.PutEnvelope(id, body)
                ? Results.NoContent()
                : Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Send one key envelope for a computer id."));

        app.MapDelete("/api/keys/envelopes/{id}", (string id, KeyStore keys) =>
        {
            keys.DeleteEnvelope(id);
            return Results.NoContent();
        });

        app.MapGet("/api/keys/recovery", (KeyStore keys) =>
            keys.GetRecovery() is { } envelope
                ? Results.Bytes(envelope, "application/octet-stream")
                : Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No recovery envelope."));

        app.MapPut("/api/keys/recovery", async (HttpContext ctx, KeyStore keys) =>
            await ReadBodyAsync(ctx, KeyStore.MaxEnvelopeBytes) is { } body && keys.PutRecovery(body)
                ? Results.NoContent()
                : Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Send the recovery envelope."));

        // Turn on encryption. For now only on a nest with no files yet (a new nest); moving an existing nest goes
        // through the separate sealed/ area (docs/ENCRYPTION.md §2.10), which comes with the apps' side.
        app.MapPost("/api/keys/seal", async (SyncStore store, KeyStore keys, IHubContext<SyncHub> hub, ILogger<KeyStore> log) =>
        {
            if (store.Sealed)
                return Json(new KeysInfo(true, keys.HasRecovery, keys.Computers()));
            if (!keys.HasRecovery || keys.Computers().Count == 0)
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest,
                    "Store the recovery envelope and this computer's envelope first.");
            if (store.Manifest.AllLive().Count > 0)
                return Error(StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                    "This nest already has files; they must be encrypted first.");
            store.Manifest.TurnOnSealed();
            log.LogInformation("This nest is encrypted now");
            await TellAsync(log, SyncHub.NestSealedMethod, () => hub.Clients.All.SendAsync(SyncHub.NestSealedMethod, CancellationToken.None));
            return Json(new KeysInfo(true, keys.HasRecovery, keys.Computers()));
        });

        app.MapGet("/api/keys/requests", (KeyStore keys) =>
            Json(keys.PendingRequests().Select(View).ToList()));

        app.MapPost("/api/keys/requests", async (KeyRequestStart? body, HttpContext ctx, KeyStore keys, IHubContext<SyncHub> hub, ILogger<KeyStore> log) =>
        {
            var name = AuthStore.CleanName(body?.Name) ?? DeviceIdentity.Of(ctx)?.Name ?? "A computer";
            if (body?.Commitment is not { } commitment || keys.StartRequest(name, commitment) is not { } request)
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                    "Send a 32-byte commitment (or too many computers are waiting; try again in a few minutes).");
            await TellAsync(log, SyncHub.KeyRequestedMethod,
                () => hub.Clients.All.SendAsync(SyncHub.KeyRequestedMethod, request.Id, request.Name, CancellationToken.None));
            return Results.Json(View(request), PairnetsJson.Options, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/api/keys/requests/{id}", (string id, KeyStore keys) =>
            keys.GetRequest(id) is { } request
                ? Json(View(request))
                : Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such unlock request (they last 15 minutes)."));

        MapMessage(app, "offer", KeyMessage.Offer);
        MapMessage(app, "reveal", KeyMessage.Reveal);
        MapMessage(app, "ring", KeyMessage.Ring);

        app.MapDelete("/api/keys/requests/{id}", (string id, KeyStore keys) =>
        {
            keys.EndRequest(id);
            return Results.NoContent();
        });
    }

    private static void MapMessage(WebApplication app, string name, KeyMessage which) =>
        app.MapPost("/api/keys/requests/{id}/" + name, (string id, KeyRequestMessage? body, KeyStore keys) =>
            body?.Data is { } data && keys.SetMessage(id, which, data)
                ? Json(View(keys.GetRequest(id)!))
                : Error(StatusCodes.Status409Conflict, ErrorCodes.Conflict,
                    "That message can't be added now (the request is gone, out of order, or already answered)."));

    /// <summary>
    /// On an encrypted nest an app that doesn't encrypt must not read or change files (it would see ids and encrypted
    /// bytes, or write plain files): its file and push calls get 426 update-required. Everything else, such as the
    /// nest's info, sign-in, the Devices list and updating, still works, so it can update itself.
    /// </summary>
    public static async Task SealedGateAsync(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path;
        var gated = path.StartsWithSegments("/api/manifest") || path.StartsWithSegments("/api/file")
            || path.StartsWithSegments("/api/upload") || path.StartsWithSegments("/api/history")
            || path.StartsWithSegments(SyncHub.Path);
        if (gated && ctx.RequestServices.GetRequiredService<SyncStore>().Sealed && !Encrypts(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await ctx.Response.WriteAsJsonAsync(new ErrorBody(ErrorCodes.UpdateRequired,
                "This nest is encrypted. Update Pairnets on this computer to keep syncing."), PairnetsJson.Options);
            return;
        }
        await next();
    }

    /// <summary>Whether the app said it encrypts (the "sealed" feature, in the header or, for the push, the query).</summary>
    internal static bool Encrypts(HttpRequest request)
    {
        var features = request.Headers[PairnetsHeaders.Features].ToString();
        if (features.Length == 0)
            features = request.Query["features"].ToString();
        return features.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(PairnetsHeaders.SealedFeature, StringComparer.Ordinal);
    }

    private static KeyRequestView View(KeyRequest r) => new(r.Id, r.Name, r.Created, r.Commitment, r.Offer, r.Reveal, r.Ring);

    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx, int max)
    {
        if (ctx.Request.ContentLength > max)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
        {
            if (buffer.Length + n > max)
                return null;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    private static async Task TellAsync(ILogger log, string method, Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not tell the apps ({Method}): {Error}", method, ex.Message);
        }
    }

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    private static IResult Error(int status, string code, string? message) =>
        Results.Json(new ErrorBody(code, message), PairnetsJson.Options, statusCode: status);
}
