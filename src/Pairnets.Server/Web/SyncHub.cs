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
/// computers; a removed computer's connections are closed right after. An app reports what it moves right now with
/// ReportTransfer(report) (about once a second while it transfers, once all zero when it stops), and the others get
/// "PeerTransfer"(name, report): real speeds for every computer, nothing guessed. Messages keep their argument
/// lists forever (older apps fail on a mismatch); new information gets a new message name.
/// </summary>
public sealed class SyncHub(BatchRegistry batches, Services.DeviceRegistry devices, Storage.AuthStore auth, Auth.DeviceKeys keys, LiveTransfers live) : Hub
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

    /// <summary>This computer's live speeds and batch: kept (in memory) and passed on to the other computers.</summary>
    public async Task ReportTransfer(TransferReport report)
    {
        if (Identity is not { } identity || report is null)
            return;
        var clean = report.Sanitized() with { AgeSeconds = 0 };
        if (live.Set(Context.ConnectionId, identity.Name, clean))
            await Clients.Others.SendAsync(PushNames.PeerTransfer, identity.Name, clean);
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
        // What the others are moving right now (only reports from the last few seconds, each saying how old it is).
        foreach (var (name, report) in live.Fresh(except: Context.ConnectionId))
            await Clients.Caller.SendAsync(PushNames.PeerTransfer, name, report);
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
        // Gone while it was moving files: it moves nothing now.
        if (live.Remove(Context.ConnectionId) is { Report.IsActive: true } gone)
            await Clients.Others.SendAsync(PushNames.PeerTransfer, gone.Name, TransferReport.Idle);
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

/// <summary>
/// The latest live report of every connected app (in memory only; nothing is saved). A report counts for
/// <see cref="FreshFor"/>; after that its computer moves nothing as far as anyone asks.
/// </summary>
public sealed class LiveTransfers(TimeProvider? clock = null)
{
    /// <summary>How long a report counts without a newer one.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);

    /// <summary>Reports closer together than this are not passed on (the apps send one a second; this only stops a flood).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(200);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _byConnection = new(StringComparer.Ordinal);

    public sealed record Entry(string Name, TransferReport Report, DateTimeOffset At);

    /// <summary>
    /// Keeps a report. False when it came too soon after the last one of the same connection (it is then not passed on),
    /// except a change between moving and not moving, which always goes through.
    /// </summary>
    public bool Set(string connectionId, string name, TransferReport report)
    {
        var now = _clock.GetUtcNow();
        var passOn = true;
        _byConnection.AddOrUpdate(connectionId, _ => new Entry(name, report, now), (_, last) =>
        {
            passOn = now - last.At >= MinInterval || last.Report.IsActive != report.IsActive;
            return passOn ? new Entry(name, report, now) : last;
        });
        return passOn;
    }

    public Entry? Remove(string connectionId) => _byConnection.TryRemove(connectionId, out var entry) ? entry : null;

    /// <summary>
    /// The reports of the last <see cref="FreshFor"/> that say something moves, by computer name, each with
    /// <see cref="TransferReport.AgeSeconds"/> set to how old it is.
    /// </summary>
    public IReadOnlyList<(string Name, TransferReport Report)> Fresh(string? except = null)
    {
        var now = _clock.GetUtcNow();
        return _byConnection
            .Where(e => e.Key != except && now - e.Value.At <= FreshFor && e.Value.Report.IsActive)
            .Select(e => (e.Value.Name, e.Value.Report with { AgeSeconds = Math.Max(0, (now - e.Value.At).TotalSeconds) }))
            .ToList();
    }
}
