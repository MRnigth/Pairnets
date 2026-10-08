using System.Net;
using Microsoft.AspNetCore.SignalR;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// The JSON API behind the nest's website (/web/api/…). Every call must reach the nest's own HTTPS name;
/// calls that change something must come from the nest's own pages (same origin); all but the sign-in
/// calls need a signed-in browser. Bodies are JSON only (no forms), and answers are never cached.
/// </summary>
public static class WebEndpoints
{
    public sealed record SetupBody(string? Code);

    public sealed record PasswordBody(string? Password, string? Current = null);

    public sealed record AllowBody(bool Allowed);

    public sealed record NameBody(string? Name);

    public sealed record MethodsView(bool Password, int Passkeys, bool Email, bool Google);

    /// <param name="CanResetPassword">This browser may set a new password without the current one (see <see cref="MayResetPassword"/>).</param>
    public sealed record StateView(bool SignedIn, bool HasSignIn, MethodsView Methods, string? NestName, string? SessionMethod,
        bool CanResetPassword = false);

    /// <summary>How a browser that opened a setup link is signed in (shown on the Security page).</summary>
    public const string SetupLinkMethod = "setup link";

    /// <summary>
    /// How long after opening a setup link that browser may set a new password without the current one. The
    /// setup link is the way back in for an owner who forgot the password (SSH, owner-link, open it, new
    /// password); the limit keeps a browser that simply stays signed in from doing it weeks later.
    /// </summary>
    public static readonly TimeSpan SetupLinkPasswordWindow = TimeSpan.FromMinutes(30);

    public sealed record DeviceView(string? Id, string Name, string? System, string? AppVersion, bool Online,
        DateTimeOffset FirstSeen, DateTimeOffset LastSeen, DateTimeOffset? LastChange, bool OwnKey);

    public sealed record PairView(string Code, string Name, string? System, string? AppVersion, string? Address,
        DateTimeOffset Created, DateTimeOffset Expires, string Status, bool SameComputer);

    public sealed record DevicesView(IReadOnlyList<DeviceView> Devices, IReadOnlyList<PairView> Pending, bool SharedTokenAllowed);

    public sealed record SessionView(string Id, string Method, string? UserAgent, DateTimeOffset Created, DateTimeOffset LastSeen, bool Current);

    public sealed record SecurityView(MethodsView Methods, DateTimeOffset? PasswordSetAt, IReadOnlyList<PasskeyEndpoints.PasskeyView> Passkeys,
        IReadOnlyList<SessionView> Sessions, bool SharedTokenAllowed, int DevicesWithOwnKey, int DevicesOnSharedToken,
        bool EmailAvailable = false, string? EmailAddress = null, bool GoogleAvailable = false, string? GoogleEmail = null);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/web/api").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var owner = http.RequestServices.GetRequiredService<OwnerAuth>();
            http.Response.Headers.CacheControl = "no-store";
            if (!owner.IsNestRequest(http))
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "The nest's website only answers on its own name.");
            if (!HttpMethods.IsGet(http.Request.Method) && !owner.IsSameOrigin(http))
                return Error(StatusCodes.Status403Forbidden, ErrorCodes.BadRequest, "This request did not come from the nest's own pages.");
            return await next(context);
        });

        // ---- open to anyone who reaches the nest (they still need a setup link or a password)

        api.MapGet("/state", (HttpContext ctx, OwnerAuth owner) =>
        {
            var session = owner.Current(ctx);
            return Json(new StateView(session is not null, owner.Store.HasSignInMethod, Methods(owner), ctx.Request.Host.Host, session?.Method,
                session is not null && owner.Store.PasswordHash is not null && MayResetPassword(owner, session)));
        });

        api.MapPost("/setup", (HttpContext ctx, SetupBody? body, OwnerAuth owner, ILogger<OwnerAuth> log) =>
        {
            if (!owner.Store.UseSetupCode(body?.Code ?? string.Empty))
            {
                log.LogWarning("A setup link was refused (used, expired or wrong) from {Address}", ctx.Connection.RemoteIpAddress);
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                    "This setup link was already used or has expired. Make a new one on the server: sudo -u pairnets /opt/pairnets/pairnets-server owner-link");
            }
            owner.SignIn(ctx, SetupLinkMethod);
            log.LogInformation("Signed in to the nest's website with a setup link from {Address}", ctx.Connection.RemoteIpAddress);
            return Results.NoContent();
        });

        api.MapPost("/signin/password", async (HttpContext ctx, PasswordBody? body, OwnerAuth owner, FailureThrottle throttle, ILogger<OwnerAuth> log) =>
        {
            var address = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
            // One owner, so wrong guesses count against everyone together (an attacker cannot start over from
            // another address), and guesses are checked one at a time: a burst of parallel guesses would all
            // read "no failures yet" before any of them was recorded.
            if (!await owner.PasswordGate.WaitAsync(TimeSpan.FromSeconds(20), ctx.RequestAborted))
                return Error(StatusCodes.Status429TooManyRequests, ErrorCodes.BadRequest, "Too many sign-in attempts. Wait a little and try again.");
            try
            {
                var delay = Max(throttle.CurrentDelay(address), throttle.CurrentDelay(AnyAddress));
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ctx.RequestAborted);
                if (!owner.CheckPassword(body?.Password))
                {
                    var failures = throttle.RecordFailure(address);
                    throttle.RecordFailure(AnyAddress);
                    log.LogWarning("Wrong password for the nest's website from {Address} ({Failures} recent failure(s))", address, failures);
                    return Error(StatusCodes.Status400BadRequest, ErrorCodes.Unauthorized, owner.Store.PasswordHash is null ? "No password is set on this nest." : "Wrong password.");
                }
                throttle.RecordSuccess(address);
                owner.SignIn(ctx, "password");
                return Results.NoContent();
            }
            finally
            {
                owner.PasswordGate.Release();
            }
        }).RequireRateLimiting(PairingEndpoints.RateLimitPolicy);

        // ---- signed in only

        var signedIn = api.MapGroup(string.Empty).AddEndpointFilter(async (context, next) =>
        {
            var owner = context.HttpContext.RequestServices.GetRequiredService<OwnerAuth>();
            return owner.Current(context.HttpContext) is null
                ? Error(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "Sign in first.")
                : await next(context);
        });

        PasskeyEndpoints.Map(api, signedIn);
        LinkedSignInEndpoints.Map(app, api, signedIn);

        signedIn.MapPost("/signout", (HttpContext ctx, OwnerAuth owner) =>
        {
            owner.SignOut(ctx);
            return Results.NoContent();
        });

        signedIn.MapGet("/devices", (HttpContext ctx, DeviceRegistry devices, DeviceKeys keys) =>
        {
            var active = keys.Store.ListDevices().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var list = devices.List()
                .Where(d => d.Id is null || active.Contains(d.Id))
                .Select(d => new DeviceView(d.Id, d.Name, d.System, d.AppVersion, d.Online, d.FirstSeen, d.LastSeen, d.LastChange, d.Id is not null))
                .ToList();
            var pending = keys.Store.ListPendingPairRequests().Select(r => View(r, ctx)).ToList();
            return Json(new DevicesView(list, pending, keys.AllowSharedToken));
        });

        signedIn.MapDelete("/devices/{id}", async (string id, HttpContext ctx, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub, ILogger<DeviceKeys> log) =>
        {
            if (keys.Store.GetDevice(id) is not { IsActive: true } device)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such computer.");
            await Endpoints.RemoveDeviceAsync(device, "removed on the nest's website", keys, devices, hub, log);
            return Results.NoContent();
        });

        signedIn.MapPatch("/devices/{id}", async (string id, NameBody? body, DeviceKeys keys, DeviceRegistry devices, IHubContext<SyncHub> hub) =>
        {
            if (AuthStore.CleanName(body?.Name) is not { } name)
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "Enter a name.");
            var renamed = await Endpoints.RenameDeviceAsync(id, name, keys, devices, hub);
            return renamed is null ? Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such computer.") : Json(new { renamed.Id, renamed.Name });
        });

        signedIn.MapGet("/pair/{code}", (string code, HttpContext ctx, AuthStore auth) =>
            auth.FindPairRequestByCode(code) is { } request
                ? Json(View(request, ctx))
                : Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "There is no computer waiting with that code. Check the code, or press Sign in on the computer again."));

        signedIn.MapPost("/pair/{code}/approve", (string code, HttpContext ctx, AuthStore auth, OwnerAuth owner, IHubContext<SyncHub> hub, ILogger<OwnerAuth> log) =>
            DecideAsync(code, approve: true, ctx, auth, owner, hub, log));

        signedIn.MapPost("/pair/{code}/deny", (string code, HttpContext ctx, AuthStore auth, OwnerAuth owner, IHubContext<SyncHub> hub, ILogger<OwnerAuth> log) =>
            DecideAsync(code, approve: false, ctx, auth, owner, hub, log));

        signedIn.MapGet("/security", (HttpContext ctx, OwnerAuth owner, DeviceKeys keys, DeviceRegistry devices) =>
        {
            var current = owner.Current(ctx)!;
            var sessions = owner.Store.ListSessions()
                .Select(s => new SessionView(s.Id, s.Method, s.UserAgent, s.Created, s.LastSeen, s.Id == current.Id)).ToList();
            var withKey = keys.Store.ListDevices().Count;
            var onShared = devices.List().Count(d => d.Id is null);
            var passkeys = owner.Store.ListPasskeys().Select(k => new PasskeyEndpoints.PasskeyView(k.Id, k.Name, k.Created, k.LastUsed)).ToList();
            return Json(new SecurityView(Methods(owner), owner.Store.PasswordSetAt, passkeys, sessions, keys.AllowSharedToken, withKey, onShared,
                owner.Options.EmailConfigured, owner.Store.OwnerEmail is { } mail ? LinkedSignInEndpoints.Mask(mail) : null,
                owner.Options.GoogleConfigured, owner.Store.GoogleAccount?.Email));
        });

        signedIn.MapPost("/password", (HttpContext ctx, PasswordBody? body, OwnerAuth owner, FailureThrottle throttle, ILogger<OwnerAuth> log) =>
            SetPassword(ctx, body, owner, throttle, log));

        signedIn.MapDelete("/password", (OwnerAuth owner) =>
        {
            if (!owner.HasOtherWayThan("password"))
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest, "You can't remove the last way to sign in. Add another one first.");
            owner.Store.SetPasswordHash(null);
            return Results.NoContent();
        });

        signedIn.MapDelete("/sessions/{id}", (string id, OwnerAuth owner) =>
            owner.Store.DeleteSession(id) ? Results.NoContent() : Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Already signed out."));

        signedIn.MapPost("/shared-token", (AllowBody body, DeviceKeys keys, DeviceRegistry devices, ILogger<DeviceKeys> log) =>
        {
            keys.AllowSharedToken = body.Allowed;
            devices.CloseDisallowed(); // computers still connected with the shared token are cut off now
            log.LogInformation("The old shared token was turned {State} on the nest's website", body.Allowed ? "on" : "off");
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Sets or changes the password (signed in). Changing one needs the current one, so a browser left signed in
    /// cannot lock you out, except right after opening a setup link: that is how an owner who forgot it gets back in.
    /// </summary>
    internal static IResult SetPassword(HttpContext ctx, PasswordBody? body, OwnerAuth owner, FailureThrottle throttle, ILogger log)
    {
        var session = owner.Current(ctx)!;
        var withoutCurrent = string.IsNullOrEmpty(body?.Current);
        var reset = false;
        if (owner.Store.PasswordHash is not null)
        {
            // A current password that is given must be right; leaving it out works only for a fresh setup link.
            reset = withoutCurrent && MayResetPassword(owner, session);
            if (!reset && !owner.CheckPassword(body?.Current))
            {
                throttle.RecordFailure(ctx.Connection.RemoteIpAddress ?? IPAddress.None);
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.Unauthorized,
                    !withoutCurrent ? "Your current password is not right."
                    : session.Method == SetupLinkMethod && owner.Now - session.Created > SetupLinkPasswordWindow
                        ? $"You opened the setup link more than {SetupLinkPasswordWindow.TotalMinutes:0} minutes ago. To set a new password without the old one, make a new link on the server: sudo -u pairnets /opt/pairnets/pairnets-server owner-link"
                    : "Type your current password.");
            }
        }
        if (OwnerAuth.PasswordProblem(body?.Password) is { } problem)
            return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, problem);
        owner.SetPassword(body!.Password!);
        log.LogInformation(reset ? "The nest's password was replaced after signing in with a setup link" : "The nest's password was set");
        return Results.NoContent();
    }

    /// <summary>
    /// True when this browser may replace the password without typing the current one: it signed in with a setup
    /// link less than <see cref="SetupLinkPasswordWindow"/> ago, and the password is older than that sign-in. So
    /// one setup link replaces a forgotten password once; a password this browser chose itself is known to it.
    /// </summary>
    internal static bool MayResetPassword(OwnerAuth owner, OwnerSession session) =>
        session.Method == SetupLinkMethod
        && owner.Now - session.Created <= SetupLinkPasswordWindow
        && (owner.Store.PasswordSetAt is not { } setAt || setAt < session.Created);

    private static async Task<IResult> DecideAsync(string code, bool approve, HttpContext ctx, AuthStore auth, OwnerAuth owner, IHubContext<SyncHub> hub, ILogger log)
    {
        if (auth.FindPairRequestByCode(code) is not { } request)
            return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "There is no computer waiting with that code.");
        var by = "the owner (" + (owner.Current(ctx)?.Method ?? "website") + ")";
        if (!auth.DecidePairRequest(request.Id, approve, by))
            return Error(StatusCodes.Status409Conflict, ErrorCodes.Conflict, request.StatusAt(owner.Now) switch
            {
                PairRequest.Expired => "This code has expired. Press Sign in on the computer again.",
                PairRequest.Denied => "This computer was already turned away.",
                _ => "This computer was already let in.",
            });
        log.LogInformation("{Name} was {Decision} on the nest's website (code {Code})", request.Name, approve ? "allowed" : "turned away", request.DisplayCode);
        try
        {
            await hub.Clients.All.SendAsync(PairingEndpoints.PairDecidedMethod, request.DisplayCode, approve, CancellationToken.None);
        }
        catch (Exception)
        {
            // Only clears the "wants to join" banners on the other computers.
        }
        return Json(View(auth.FindPairRequestByCode(code)!, ctx));
    }

    private static PairView View(PairRequest r, HttpContext ctx) => new(
        r.DisplayCode, r.Name, r.System, r.AppVersion, r.Address, r.Created, r.Expires, r.StatusAt(DateTimeOffset.UtcNow),
        SameComputer: r.Address is not null && r.Address == ctx.Connection.RemoteIpAddress?.ToString());

    private static MethodsView Methods(OwnerAuth owner)
    {
        var (password, passkeys, email, google) = owner.UsableMethods();
        return new MethodsView(password, passkeys, email, google);
    }

    /// <summary>
    /// A "go back to this page after signing in" value as a path on this site, or null. Same rule as the
    /// website's own safeNext: it must start with one "/" (never "//" or "/\", which browsers read as another
    /// site), so a sign-in can never be bounced somewhere else.
    /// </summary>
    internal static string? SafeNextPath(string? value) =>
        value is ['/', not ('/' or '\\'), ..] && value.Length <= 512
            && !value.Contains('\\', StringComparison.Ordinal) && !value.Any(char.IsControl)
            && Uri.TryCreate(value, UriKind.Relative, out _)
        ? value
        : null;

    /// <summary>Stands for "everyone" in the failure counter: wrong passwords from any address add up here.</summary>
    private static readonly IPAddress AnyAddress = IPAddress.Broadcast;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), PairnetsJson.Options, statusCode: status);
}
