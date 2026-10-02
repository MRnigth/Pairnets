using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Net.Http.Headers;
using Tether.Core;
using Tether.Server.Services;
using Tether.Server.Storage;

namespace Tether.Server.Web;

/// <summary>HTTP API. All JSON is camelCase; every non-2xx response has an {code, message} body.</summary>
public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Text("ok", "text/plain"));

        app.MapGet("/api/info", (SyncStore store, ServerUpdater updater) =>
        {
            var (free, total) = ServerUpdater.DiskSpace(store.Paths.DataDir);
            return Json(new ServerInfo(store.ServerId, store.CurrentVersion, TetherInfo.ApiVersion,
                TetherInfo.ProductVersion, free, total, updater.GetStatus()));
        });

        // "Update server" in the apps: asks the root-owned updater to install the newest release.
        app.MapPost("/api/update", (ServerUpdater updater) => updater.Request() switch
        {
            ServerUpdater.RequestOutcome.Requested => Results.Json(updater.GetStatus(), TetherJson.Options, statusCode: StatusCodes.Status202Accepted),
            ServerUpdater.RequestOutcome.TooSoon => Error(StatusCodes.Status429TooManyRequests, ErrorCodes.BadRequest, "An update was requested less than a minute ago."),
            _ => Error(StatusCodes.Status409Conflict, ErrorCodes.UpdaterMissing,
                "This server cannot update itself yet. Run the install command on the server once."),
        });

        app.MapGet("/api/manifest", (HttpContext ctx, SyncStore store) =>
        {
            long? since = null;
            var raw = ctx.Request.Query["since"].ToString();
            if (raw.Length > 0)
            {
                if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "since must be a non-negative integer.");
                since = parsed;
            }
            var (entries, version) = store.ReadManifest(since);
            ctx.Response.Headers[TetherHeaders.ServerId] = store.ServerId;
            ctx.Response.Headers[TetherHeaders.Version] = version.ToString(CultureInfo.InvariantCulture);
            return Json(entries);
        });

        app.MapGet("/api/file", async (HttpContext ctx, SyncStore store) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            var opened = await store.OpenReadAsync(path, ctx.RequestAborted);
            if (opened is null)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such file.");
            var entry = opened.Entry;
            ctx.Response.Headers[TetherHeaders.Hash] = entry.Hash;
            ctx.Response.Headers["X-Tether-Modified-Ms"] = entry.ModifiedMs.ToString(CultureInfo.InvariantCulture);
            return Results.File(opened.Stream, "application/octet-stream",
                lastModified: DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedMs),
                entityTag: new EntityTagHeaderValue("\"" + entry.Hash + "\""),
                enableRangeProcessing: true);
        });

        app.MapPut("/api/file", async (HttpContext ctx, SyncStore store, IHubContext<SyncHub> hub) =>
        {
            var bodySize = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
                bodySize.MaxRequestBodySize = null;

            var path = ctx.Request.Query["path"].ToString();
            var baseValue = ctx.Request.Query["base"].ToString();
            long? mtime = null;
            var rawMtime = ctx.Request.Query["mtime"].ToString();
            if (rawMtime.Length > 0)
            {
                if (!long.TryParse(rawMtime, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < -62_135_596_800_000 || parsed > 253_402_300_799_999)
                    return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "mtime must be Unix milliseconds.");
                mtime = parsed;
            }

            ChangeResult result;
            try
            {
                result = await store.PutAsync(path, baseValue, mtime, ctx.Request.Body, ctx.RequestAborted);
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                // The body stalled (see SyncOptions.UploadStallTimeout); the temp file is already gone.
                return Error(StatusCodes.Status408RequestTimeout, ErrorCodes.BadRequest, "Upload stalled and was discarded.");
            }
            return await ToResultAsync(result, ctx, path, hub);
        });

        app.MapDelete("/api/file", async (HttpContext ctx, SyncStore store, IHubContext<SyncHub> hub) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            var result = await store.DeleteAsync(path, ctx.Request.Query["base"].ToString());
            return await ToResultAsync(result, ctx, path, hub);
        });

        app.MapGet("/api/history", (HttpContext ctx, SyncStore store) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (!Tether.Core.Paths.PathRules.IsValid(path))
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidName, "Invalid path.");
            return Json(store.ListHistory(path));
        });

        app.MapPost("/api/history/restore", async (HttpContext ctx, SyncStore store, IHubContext<SyncHub> hub) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            var result = await store.RestoreAsync(path, ctx.Request.Query["id"].ToString(), ctx.RequestAborted);
            return await ToResultAsync(result, ctx, path, hub);
        });

        app.MapHub<SyncHub>(SyncHub.Path);
    }

    private static async Task<IResult> ToResultAsync(ChangeResult result, HttpContext ctx, string path, IHubContext<SyncHub> hub)
    {
        switch (result.Status)
        {
            case ChangeStatus.Ok:
                await BroadcastAsync(ctx, hub, path);
                return Json(result.Entry!);
            case ChangeStatus.Unchanged:
                return Json(result.Entry!);
            case ChangeStatus.Conflict:
                return Error(StatusCodes.Status409Conflict, ErrorCodes.Conflict, result.Message);
            case ChangeStatus.CaseCollision:
                return Error(StatusCodes.Status409Conflict, ErrorCodes.CaseCollision, result.Message);
            case ChangeStatus.InvalidName:
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.InvalidName, result.Message);
            case ChangeStatus.NotFound:
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, result.Message);
            default:
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, result.Message);
        }
    }

    private static async Task BroadcastAsync(HttpContext ctx, IHubContext<SyncHub> hub, string path)
    {
        var device = DeviceId(ctx);
        try
        {
            await hub.Clients.All.SendAsync(SyncHub.ChangedMethod, device, path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILogger<SyncHub>>().LogWarning("Broadcast failed: {Error}", ex.Message);
        }
    }

    /// <summary>The device name sent by the client (URL-escaped on the wire so any name fits in a header).</summary>
    public static string DeviceId(HttpContext ctx)
    {
        var raw = ctx.Request.Headers[TetherHeaders.DeviceId].ToString();
        if (raw.Length == 0)
            return "unknown";
        try
        {
            var value = Uri.UnescapeDataString(raw);
            return value.Length > 128 ? value[..128] : value;
        }
        catch (UriFormatException)
        {
            return "unknown";
        }
    }

    private static IResult Json<T>(T value) => Results.Json(value, TetherJson.Options);

    private static IResult Error(int status, string code, string? message) =>
        Results.Json(new ErrorBody(code, message), TetherJson.Options, statusCode: status);
}
