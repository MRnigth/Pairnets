using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Tether.Core;

namespace Tether.Server.Web;

/// <summary>
/// Push channel. The server sends "Changed"(deviceId, path) after every change, and relays
/// "big batch" announcements: a device about to upload many files calls BatchStarted(count) and
/// BatchFinished(), and the others get "PeerBatch"(deviceId, count, active) so they can wait and
/// download the batch in one go. A batch ends by itself when its device disconnects.
/// </summary>
public sealed class SyncHub(BatchRegistry batches) : Hub
{
    public const string Path = "/hub";
    public const string ChangedMethod = "Changed";
    public const string PeerBatchMethod = "PeerBatch";

    private string DeviceId
    {
        get
        {
            var raw = Context.GetHttpContext()?.Request.Headers[TetherHeaders.DeviceId].ToString();
            return string.IsNullOrWhiteSpace(raw) ? "another computer" : Uri.UnescapeDataString(raw);
        }
    }

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
        foreach (var (connection, batch) in batches.Active())
        {
            if (connection != Context.ConnectionId)
                await Clients.Caller.SendAsync(PeerBatchMethod, batch.Device, batch.Count, true);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
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
