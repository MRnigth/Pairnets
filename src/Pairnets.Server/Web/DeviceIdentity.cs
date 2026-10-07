using System.Security.Claims;
using Pairnets.Core;

namespace Pairnets.Server.Web;

/// <summary>How a request proved it may sync: a computer's own key, or the old shared token.</summary>
public enum DeviceAuthKind
{
    DeviceKey,
    SharedToken,
}

/// <summary>
/// The computer behind an authenticated request. With its own key the name comes from the server's
/// records and can be trusted; with the shared token it is whatever the app sent in X-Device-Id.
/// </summary>
public sealed record DeviceIdentity(string? Id, string Name, DeviceAuthKind Kind)
{
    private const string ItemKey = "pairnets.device";
    private const string KindClaim = "pairnets:kind";

    /// <summary>The key the Devices list and the push channel use for this computer.</summary>
    public string RegistryKey => Id is null ? Name : "id:" + Id;

    public void AttachTo(HttpContext context)
    {
        context.Items[ItemKey] = this;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Id ?? string.Empty),
            new Claim(ClaimTypes.Name, Name),
            new Claim(KindClaim, Kind.ToString()),
        ], authenticationType: "Pairnets"));
    }

    public static DeviceIdentity? Of(HttpContext context) => context.Items[ItemKey] as DeviceIdentity;

    /// <summary>The identity a hub connection was opened with (the claims survive for the whole connection).</summary>
    public static DeviceIdentity? Of(ClaimsPrincipal? user)
    {
        if (user?.FindFirst(ClaimTypes.Name)?.Value is not { Length: > 0 } name
            || !Enum.TryParse<DeviceAuthKind>(user.FindFirst(KindClaim)?.Value, out var kind))
            return null;
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return new DeviceIdentity(string.IsNullOrEmpty(id) ? null : id, name, kind);
    }

    /// <summary>The name an app sent in X-Device-Id (URL-escaped on the wire), or "unknown".</summary>
    public static string HeaderName(HttpContext context)
    {
        var raw = context.Request.Headers[PairnetsHeaders.DeviceId].ToString();
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
}
