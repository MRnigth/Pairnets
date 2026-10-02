using Microsoft.AspNetCore.SignalR;

namespace Tether.Server.Web;

/// <summary>Server-to-client notifications. Clients only listen for "Changed"(deviceId, path).</summary>
public sealed class SyncHub : Hub
{
    public const string Path = "/hub";
    public const string ChangedMethod = "Changed";
}
