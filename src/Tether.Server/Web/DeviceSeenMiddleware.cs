using Tether.Core;
using Tether.Server.Services;

namespace Tether.Server.Web;

/// <summary>
/// Notes which computer made each accepted request (X-Device-Id, plus X-Tether-App when sent), so
/// the apps can show when the other computer was last seen. Runs after the token check; the health
/// check needs no token, so it is never counted.
/// </summary>
public sealed class DeviceSeenMiddleware(RequestDelegate next, DeviceRegistry devices)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
            && DeviceRegistry.NameFromHeader(context.Request.Headers[TetherHeaders.DeviceId].ToString()) is { } device)
            devices.Seen(device, context.Request.Headers[TetherHeaders.App].ToString());
        return next(context);
    }
}
