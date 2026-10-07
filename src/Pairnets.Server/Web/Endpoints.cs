using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Net.Http.Headers;
using Pairnets.Core;
using Pairnets.Core.Hashing;
using Pairnets.Core.Legacy;
using Pairnets.Server.Auth;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>HTTP API. All JSON is camelCase; every non-2xx response has an {code, message} body.</summary>
public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Text("ok", "text/plain"));

        // No sign-in needed: lets an app (or the nest's web page) recognise a Pairnets server and find its HTTPS name.
        app.MapGet("/api/hello", (SyncOptions options) => Json(new ServerHello(PairnetsInfo.ProductName, PairnetsInfo.ApiVersion,
            PairnetsInfo.ProductVersion, options.PublicUrl, DeviceKeys: true, SignIn: options.PublicUrl is not null)));

        // Who the server thinks this computer is (the apps adopt a name changed on the nest).
        app.MapGet("/api/me", (HttpContext ctx) => DeviceIdentity.Of(ctx) is { } me
            ? Json(new DeviceMe(me.Id, me.Name, me.Kind == DeviceAuthKind.DeviceKey ? DeviceMe.KindDeviceKey : DeviceMe.KindSharedToken))
            : Error(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "Not signed in."));

        // A computer set up with the shared token trades it for its own key, silently, at its next start.
        app.MapPost("/api/devices/upgrade", (HttpContext ctx, DeviceKeys keys, DeviceRegistry devices, DeviceNameRequest? body) =>
        {
            if (DeviceIdentity.Of(ctx) is not { Kind: DeviceAuthKind.SharedToken } shared)
                return Error(StatusCodes.Status409Conflict, ErrorCodes.WrongCredential, "This computer already has its own key.");
            var name = AuthStore.CleanName(body?.Name) ?? AuthStore.CleanName(shared.Name);
            if (name is null)
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Send this computer's name.");
            var (version, system) = DeviceRegistry.ParseClient(ctx.Request.Headers[PairnetsHeaders.Client].ToString());
            // Asked before but the app never saved its key (it crashed, say): same computer, new key, no "(2)".
            PairedDevice device;
            string key;
            if (keys.Store.FindActiveByName(name) is { ApprovedBy: SharedTokenApproval } earlier && keys.Store.ReplaceKey(earlier.Id) is { } replaced)
            {
                (device, key) = (earlier, replaced);
                keys.Forget(earlier.Id);
            }
            else
            {
                (device, key) = keys.Store.AddDevice(name, system, SharedTokenApproval);
            }
            devices.Adopt(shared.Name, new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey).RegistryKey, device.Name);
            ctx.RequestServices.GetRequiredService<ILogger<DeviceKeys>>()
                .LogInformation("{Name} switched from the shared token to its own key (app {Version})", device.Name, version);
            return Json(new DeviceKeyGrant(device.Id, device.Name, key));
        });

        // Remove a computer (the Devices page, or "Sign out of this computer"): its key stops working at once.
        app.MapDelete("/api/devices/{id}", async (string id, HttpContext ctx, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub) =>
        {
            if (keys.Store.GetDevice(id) is not { IsActive: true } device)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such computer.");
            await RemoveDeviceAsync(device, $"removed by {DeviceIdentity.Of(ctx)?.Name}", keys, devices, hub,
                ctx.RequestServices.GetRequiredService<ILogger<DeviceKeys>>());
            return Results.NoContent();
        });

        // Rename this computer (its key stays); the name is made unique among the active computers.
        app.MapPatch("/api/devices/me", async (HttpContext ctx, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub, DeviceNameRequest? body) =>
        {
            if (DeviceIdentity.Of(ctx) is not { Kind: DeviceAuthKind.DeviceKey, Id: { } id })
                return Error(StatusCodes.Status409Conflict, ErrorCodes.WrongCredential, "Only a computer with its own key can be renamed.");
            if (AuthStore.CleanName(body?.Name) is not { } name)
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Send a name.");
            var renamed = await RenameDeviceAsync(id, name, keys, devices, hub);
            return renamed is null
                ? Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such computer.")
                : Json(new DeviceMe(renamed.Id, renamed.Name, DeviceMe.KindDeviceKey));
        });

        app.MapGet("/api/info", (SyncStore store, ServerUpdater updater) =>
        {
            var (free, total) = ServerUpdater.DiskSpace(store.Paths.DataDir);
            return Json(new ServerInfo(store.ServerId, store.CurrentVersion, PairnetsInfo.ApiVersion,
                PairnetsInfo.ProductVersion, free, total, updater.GetStatus()));
        });

        // "Update server" in the apps: asks the root-owned updater to install the newest release.
        app.MapPost("/api/update", (ServerUpdater updater) => updater.Request() switch
        {
            ServerUpdater.RequestOutcome.Requested => Results.Json(updater.GetStatus(), PairnetsJson.Options, statusCode: StatusCodes.Status202Accepted),
            ServerUpdater.RequestOutcome.TooSoon => Error(StatusCodes.Status429TooManyRequests, ErrorCodes.BadRequest, "An update was requested less than a minute ago."),
            _ => Error(StatusCodes.Status409Conflict, ErrorCodes.UpdaterMissing,
                "This server cannot update itself yet. Run the install command on the server once."),
        });

        // The apps' Devices page and their "this computer ⇄ server ⇄ other computer" picture.
        app.MapGet("/api/devices", (DeviceRegistry devices, DeviceKeys keys) =>
        {
            // Computers removed from the command line (another process) drop out here too.
            var active = keys.Store.ListDevices().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            return Json(devices.List().Where(d => d.Id is null || active.Contains(d.Id)).ToList());
        });

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
            ctx.Response.Headers[PairnetsHeaders.ServerId] = ctx.Response.Headers[TetherNames.ServerIdHeader] = store.ServerId;
            ctx.Response.Headers[PairnetsHeaders.Version] = ctx.Response.Headers[TetherNames.VersionHeader] = version.ToString(CultureInfo.InvariantCulture);
            return Json(entries);
        });

        app.MapGet("/api/file", async (HttpContext ctx, SyncStore store) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            var opened = await store.OpenReadAsync(path, ctx.RequestAborted);
            if (opened is null)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such file.");
            var entry = opened.Entry;
            // Also under the old Tether names, so an app that is not updated yet keeps syncing.
            ctx.Response.Headers[PairnetsHeaders.Hash] = ctx.Response.Headers[TetherNames.HashHeader] = entry.Hash;
            ctx.Response.Headers[PairnetsHeaders.ModifiedMs] = ctx.Response.Headers[TetherNames.ModifiedMsHeader] = entry.ModifiedMs.ToString(CultureInfo.InvariantCulture);
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
                UploadSessions.StartStatus.Started => Results.Json(new UploadStatus(started.Id!, 0), PairnetsJson.Options, statusCode: StatusCodes.Status201Created),
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
            if (!Pairnets.Core.Paths.PathRules.IsValid(path))
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

    /// <summary>Removes a computer: key, cached lookups, Devices entry and push connections; tells the other apps.</summary>
    public static async Task RemoveDeviceAsync(PairedDevice device, string why, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub, ILogger log)
    {
        keys.Store.RemoveDevice(device.Id);
        keys.Forget(device.Id);
        var key = new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey).RegistryKey;
        try
        {
            // First the message (the removed app stops at once), then its connections are closed.
            await hub.Clients.All.SendAsync(SyncHub.DeviceRemovedMethod, device.Id, device.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not announce the removal: {Error}", ex.Message);
        }
        devices.Remove(key);
        log.LogInformation("Computer {Name} was removed ({Why})", device.Name, why);
    }

    public static async Task<PairedDevice?> RenameDeviceAsync(string id, string name, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub)
    {
        var renamed = keys.Store.RenameDevice(id, name);
        if (renamed is null)
            return null;
        keys.Forget(id);
        devices.Seen(new DeviceIdentity(renamed.Id, renamed.Name, DeviceAuthKind.DeviceKey).RegistryKey, renamed.Name, null);
        try
        {
            await hub.Clients.All.SendAsync(SyncHub.DeviceRenamedMethod, renamed.Id, renamed.Name, CancellationToken.None);
        }
        catch (Exception)
        {
            // The app also reads its name at its next start.
        }
        return renamed;
    }

    private static async Task BroadcastAsync(HttpContext ctx, IHubContext<SyncHub> hub, string path)
    {
        var device = DeviceId(ctx);
        ctx.RequestServices.GetRequiredService<DeviceRegistry>().Changed(DeviceIdentity.Of(ctx)?.RegistryKey ?? device);
        try
        {
            await hub.Clients.All.SendAsync(SyncHub.ChangedMethod, device, path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILogger<SyncHub>>().LogWarning("Broadcast failed: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// The computer's name: from the server's records for a computer with its own key, otherwise the name the
    /// app sent (URL-escaped on the wire so any name fits in a header).
    /// </summary>
    public static string DeviceId(HttpContext ctx) => DeviceIdentity.Of(ctx)?.Name ?? DeviceIdentity.HeaderName(ctx);

    /// <summary>How computers that traded the shared token for their own key were let in (shown on the nest).</summary>
    public const string SharedTokenApproval = "shared token";

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

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    private static IResult Error(int status, string code, string? message) =>
        Results.Json(new ErrorBody(code, message), PairnetsJson.Options, statusCode: status);
}
