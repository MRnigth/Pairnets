using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Pairnets.Core;

namespace Pairnets.Server.Web;

/// <summary>
/// Push channel. The server sends "Changed"(deviceId, path) after every change, and relays
/// "big batch" announcements: a device about to upload many files calls BatchStarted(count) and
/// BatchFinished(), and the others get "PeerBatch"(deviceId, count, active) so they can wait and
/// download the batch in one go. A batch ends by itself when its device disconnects.
/// "DeviceRemoved"(id, name) and "DeviceRenamed"(id, name) tell the apps about changes to the list of
/// computers; a removed computer's connections are closed right after. Messages keep their argument
/// lists forever (older apps fail on a mismatch); new information gets a new message name.
/// </summary>
public sealed class SyncHub(BatchRegistry batches, Services.DeviceRegistry devices, Storage.AuthStore auth, Auth.DeviceKeys keys) : Hub
{
    public const string Path = "/hub";
    public const string ChangedMethod = "Changed";
    public const string PeerBatchMethod = "PeerBatch";
    public const string DeviceRemovedMethod = "DeviceRemoved";
    public const string DeviceRenamedMethod = "DeviceRenamed";

    /// <summary>The computer behind this connection, from the key or token it connected with.</summary>
    private DeviceIdentity? Identity => DeviceIdentity.Of(Context.User);

    private string DeviceId => Identity?.Name ?? "another computer";

    public async Task BatchStarted(int count)
    {
        var device = DeviceId;
        count = Math.Clamp(count, 1, 10_000_000);
        batches.Set(Context.ConnectionId, device, count);
        await Clients.Others.SendAsync(PeerBatchMethod, device, count, true);
    }

    public async Task BatchFinished()
    {
        if (batches.Remove(Context.ConnectionId) is { } batch)
            await Clients.Others.SendAsync(PeerBatchMethod, batch.Device, 0, false);
    }

    public override async Task OnConnectedAsync()
    {
        if (Identity is { } identity)
        {
            devices.Connected(Context.ConnectionId, identity.RegistryKey, Context.Abort, () => StillAllowed(identity));
            // Removed while this connection was being set up: the removal may have looked for connections just
            // before this one was registered, so check again now that it is.
            if (identity.Id is { } id && auth.GetDevice(id) is not { IsActive: true })
            {
                devices.Disconnected(Context.ConnectionId);
                Context.Abort();
                return;
            }
        }
        foreach (var (connection, batch) in batches.Active())
        {
            if (connection != Context.ConnectionId)
                await Clients.Caller.SendAsync(PeerBatchMethod, batch.Device, batch.Count, true);
        }
        await base.OnConnectedAsync();
    }

    /// <summary>Whether the computer behind a live connection may still be connected (asked every few seconds).</summary>
    private bool StillAllowed(DeviceIdentity identity)
    {
        if (identity.Kind == DeviceAuthKind.SharedToken)
            return auth.AllowSharedToken;
        if (identity.Id is { } id && auth.GetDevice(id) is { IsActive: true })
            return true;
        if (identity.Id is { } removed)
            keys.Forget(removed); // HTTP with its key stops now too, not up to 30 s later
        return false;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        devices.Disconnected(Context.ConnectionId);
        if (batches.Remove(Context.ConnectionId) is { } batch)
            await Clients.Others.SendAsync(PeerBatchMethod, batch.Device, 0, false);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Big batches currently being uploaded, by hub connection.</summary>
public sealed class BatchRegistry
{
    public sealed record Batch(string Device, int Count);

    private readonly ConcurrentDictionary<string, Batch> _byConnection = new(StringComparer.Ordinal);

    public void Set(string connectionId, string device, int count) => _byConnection[connectionId] = new Batch(device, count);

    public Batch? Remove(string connectionId) => _byConnection.TryRemove(connectionId, out var batch) ? batch : null;

    public IEnumerable<KeyValuePair<string, Batch>> Active() => _byConnection.ToArray();
}
