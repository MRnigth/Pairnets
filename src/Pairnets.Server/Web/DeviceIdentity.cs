using System.Security.Claims;
using Pairnets.Core;
using Pairnets.Server.Storage;

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
/// <see cref="Access"/> is what it may do, from the server's records too: only a friend's computer has less than the
/// owner's (the shared token, like every way in that is not a computer's own key, is always the owner).
/// </summary>
public sealed record DeviceIdentity(string? Id, string Name, DeviceAuthKind Kind, DeviceAccess? Access = null)
{
    private const string ItemKey = "pairnets.device";
    private const string KindClaim = "pairnets:kind";
    private const string AccessClaim = "pairnets:access";
    private const string FoldersClaim = "pairnets:folders";

    /// <summary>The key the Devices list and the push channel use for this computer.</summary>
    public string RegistryKey => Id is null ? Name : "id:" + Id;

    /// <summary>What this computer may do (the owner's access unless its key says less).</summary>
    public DeviceAccess AccessOrOwner => Kind == DeviceAuthKind.DeviceKey && Access is { } access ? access : DeviceAccess.Full;

    public void AttachTo(HttpContext context)
    {
        context.Items[ItemKey] = this;
        var access = AccessOrOwner;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Id ?? string.Empty),
            new Claim(ClaimTypes.Name, Name),
            new Claim(KindClaim, Kind.ToString()),
            new Claim(AccessClaim, access.Level),
            new Claim(FoldersClaim, DeviceAccess.FoldersToJson(access.Folders) ?? string.Empty),
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
        // A connection from before its claims had access in them would be the owner's; there is none (they are made here).
        var level = user.FindFirst(AccessClaim)?.Value ?? DeviceAccess.Owner;
        var folders = user.FindFirst(FoldersClaim)?.Value is { Length: > 0 } json ? DeviceAccess.FoldersFromJson(json) : null;
        var access = level == DeviceAccess.Owner ? DeviceAccess.Full : new DeviceAccess(DeviceAccess.IsLevel(level) ? level : DeviceAccess.View, folders);
        return new DeviceIdentity(string.IsNullOrEmpty(id) ? null : id, name, kind, access);
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
