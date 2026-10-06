using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Net.Http.Headers;
using Tether.Core;
using Tether.Core.Hashing;
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

        // The apps' Devices page and their "this computer ⇄ server ⇄ other computer" picture.
        app.MapGet("/api/devices", (DeviceRegistry devices) => Json(devices.List()));

        // Debug mode in the apps: why an update did not happen (status, request, path unit, update.log).
        app.MapGet("/api/update/diagnostics", (ServerUpdater updater, SyncOptions options) => Json(updater.GetDiagnostics(options.Token)));

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
            if (!TryReadMtime(ctx, out var mtime))
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "mtime must be Unix milliseconds.");

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

        // Uploads in pieces: big files through proxies that cap one request (Cloudflare: 100 MB), and
        // an upload cut off half-way continues where it stopped. See UploadSessions.
        app.MapPost("/api/upload", async (HttpContext ctx, UploadSessions uploads) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (!TryReadMtime(ctx, out var mtime))
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "mtime must be Unix milliseconds.");
            if (!long.TryParse(ctx.Request.Query["size"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "size must be a non-negative integer.");
            var started = await uploads.StartAsync(path, ctx.Request.Query["base"].ToString(), size, mtime, ctx.RequestAborted);
            return started.Status switch
            {
                UploadSessions.StartStatus.Started => Results.Json(new UploadStatus(started.Id!, 0), TetherJson.Options, statusCode: StatusCodes.Status201Created),
                UploadSessions.StartStatus.TooMany => Error(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Busy, "Too many uploads are in progress. Try again later."),
                _ => ErrorFor(started.Rejection!),
            };
        });

        app.MapPut("/api/upload/{id}", async (string id, HttpContext ctx, UploadSessions uploads) =>
        {
            var bodySize = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
                bodySize.MaxRequestBodySize = UploadSessions.MaxChunkBytes;
            if (ctx.Request.ContentLength > UploadSessions.MaxChunkBytes)
                return Error(StatusCodes.Status413PayloadTooLarge, ErrorCodes.TooLarge, $"A piece may be at most {UploadSessions.MaxChunkBytes} bytes.");
            if (!long.TryParse(ctx.Request.Query["offset"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "offset must be a non-negative integer.");

            UploadSessions.ChunkResult result;
            try
            {
                result = await uploads.AppendAsync(id, offset, ctx.Request.Body, ctx.RequestAborted);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, ErrorCodes.TooLarge, $"A piece may be at most {UploadSessions.MaxChunkBytes} bytes.");
            }
            return result.Status switch
            {
                UploadSessions.ChunkStatus.Ok => Json(new UploadStatus(id, result.Received)),
                UploadSessions.ChunkStatus.WrongOffset => Error(StatusCodes.Status409Conflict, ErrorCodes.UploadOffset,
                    $"The server has {result.Received.ToString(CultureInfo.InvariantCulture)} bytes of this upload; continue from there."),
                UploadSessions.ChunkStatus.Stalled => Error(StatusCodes.Status408RequestTimeout, ErrorCodes.BadRequest, "The piece stalled; what arrived is kept."),
                UploadSessions.ChunkStatus.TooMuchData => Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "More data than the declared size; the upload was dropped."),
                _ => UploadNotFound(),
            };
        });

        app.MapGet("/api/upload/{id}", (string id, UploadSessions uploads) =>
            uploads.Received(id) is { } received ? Json(new UploadStatus(id, received)) : UploadNotFound());

        app.MapPost("/api/upload/{id}/commit", async (string id, HttpContext ctx, UploadSessions uploads, IHubContext<SyncHub> hub) =>
        {
            var hash = ctx.Request.Query["hash"].ToString();
            if (!ContentHash.IsValid(hash))
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "hash must be a SHA-256 hash.");
            var result = await uploads.CommitAsync(id, hash, ctx.RequestAborted);
            return result.Status switch
            {
                UploadSessions.CommitStatus.Done => await ToResultAsync(result.Change!, ctx, result.Change!.Entry?.Path ?? string.Empty, hub),
                UploadSessions.CommitStatus.Incomplete => Error(StatusCodes.Status409Conflict, ErrorCodes.UploadOffset, result.Message),
                UploadSessions.CommitStatus.HashMismatch => Error(StatusCodes.Status400BadRequest, ErrorCodes.UploadMismatch, result.Message),
                _ => UploadNotFound(),
            };
        });

        app.MapDelete("/api/upload/{id}", (string id, UploadSessions uploads) =>
        {
            uploads.Abort(id);
            return Results.NoContent();
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
            default:
                return ErrorFor(result);
        }
    }

    /// <summary>The error answer for a change the store refused.</summary>
    private static IResult ErrorFor(ChangeResult result)
    {
        switch (result.Status)
        {
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
        ctx.RequestServices.GetRequiredService<DeviceRegistry>().Changed(device);
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

    /// <summary>Reads the optional "mtime" (Unix milliseconds). False when it is present but invalid.</summary>
    private static bool TryReadMtime(HttpContext ctx, out long? mtime)
    {
        mtime = null;
        var raw = ctx.Request.Query["mtime"].ToString();
        if (raw.Length == 0)
            return true;
        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            || parsed < -62_135_596_800_000 || parsed > 253_402_300_799_999)
            return false;
        mtime = parsed;
        return true;
    }

    private static IResult UploadNotFound() =>
        Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such upload (it was finished, dropped after an hour without data, or the server restarted).");

    private static IResult Json<T>(T value) => Results.Json(value, TetherJson.Options);

    private static IResult Error(int status, string code, string? message) =>
        Results.Json(new ErrorBody(code, message), TetherJson.Options, statusCode: status);
}
