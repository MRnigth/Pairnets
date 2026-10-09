using Microsoft.AspNetCore.SignalR;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// Adding a computer (like signing in to a TV with a code): the new app asks to join and shows a code,
/// you approve it on the nest's website while signed in, and the app collects its own key. Neither call
/// needs a key; both are rate-limited per address. The poll secret travels in the request body, never in
/// the URL, so it never reaches a log.
/// </summary>
public static class PairingEndpoints
{
    public const string RateLimitPolicy = "pairing";
    public const string PairRequestedMethod = "PairRequested";
    public const string PairDecidedMethod = "PairDecided";

    /// <summary>At most this many requests may wait at once (from one address / in total).</summary>
    public const int MaxPendingPerAddress = 3;
    public const int MaxPending = 20;

    public const int PollIntervalSeconds = 2;

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/pair/start", async (HttpContext ctx, PairStartRequest? body, AuthStore auth, SyncOptions options, IHubContext<SyncHub> hub, ILogger<PairStartRequest> log) =>
        {
            if (options.RelayMode)
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest,
                    "This nest is linked to a Pairnets account: sign this computer in with that account (Continue with email or Google).");
            if (options.PublicUrl is null)
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest,
                    "This nest has no website yet to approve computers on. Give it its own public name first (install.sh --public-url https://nest.example.com).");
            var name = AuthStore.CleanName(body?.Name);
            if (name is null)
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Send this computer's name.");
            var address = ctx.Connection.RemoteIpAddress?.ToString();
            if (auth.ListPendingPairRequests(address).Count >= MaxPendingPerAddress || auth.ListPendingPairRequests().Count >= MaxPending)
                return Error(StatusCodes.Status429TooManyRequests, ErrorCodes.BadRequest, "Too many computers are waiting to be approved. Try again in a few minutes.");

            var (request, secret) = auth.CreatePairRequest(name, body?.System, body?.AppVersion, address);
            log.LogInformation("{Name} ({System}) asks to join from {Address}: code {Code}", request.Name, request.System, address, request.DisplayCode);
            try
            {
                // The other computers show "LAPTOP-2 wants to join" with a link to the approval page.
                await hub.Clients.All.SendAsync(PairRequestedMethod, request.DisplayCode, request.Name, request.System ?? string.Empty, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogDebug("Could not announce the request: {Error}", ex.Message);
            }
            return Json(new PairStartResponse(secret, request.DisplayCode,
                options.PublicUrl is { } url ? $"{url}/link?code={request.DisplayCode}" : null,
                (int)AuthStore.PairRequestLifetime.TotalSeconds, PollIntervalSeconds));
        }).RequireRateLimiting(RateLimitPolicy);

        app.MapPost("/api/pair/poll", (PairPollRequest? body, AuthStore auth, DeviceRegistry devices, ILogger<PairPollRequest> log) =>
        {
            var request = auth.FindPairRequestBySecret(body?.PollToken);
            if (request is null)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Unknown request. Start again.");
            var status = request.StatusAt(DateTimeOffset.UtcNow);
            if (status == PairRequest.Approved)
            {
                if (auth.DeliverPairRequest(request.Id) is not { } delivered)
                    return Json(new PairPollResponse(PairPollResponse.Used)); // collected a moment ago by another poll
                var (device, key) = delivered;
                // A computer that used the shared token under this name keeps its history in the Devices list.
                devices.Adopt(device.Name, new DeviceIdentity(device.Id, device.Name, DeviceAuthKind.DeviceKey).RegistryKey, device.Name);
                log.LogInformation("{Name} joined the nest ({By})", device.Name, device.ApprovedBy);
                return Json(new PairPollResponse(PairPollResponse.Approved, device.Id, device.Name, key));
            }
            switch (status)
            {
                case PairRequest.Pending:
                    return Json(new PairPollResponse(PairPollResponse.Pending));
                case PairRequest.Denied:
                    return Json(new PairPollResponse(PairPollResponse.Denied));
                case PairRequest.Delivered:
                    return Json(new PairPollResponse(PairPollResponse.Used));
                default:
                    return Json(new PairPollResponse(PairPollResponse.Expired));
            }
        }).RequireRateLimiting(RateLimitPolicy);
    }

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), PairnetsJson.Options, statusCode: status);
}
