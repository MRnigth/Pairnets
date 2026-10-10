using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Pairnets.Core;
using Pairnets.Server.Web;

namespace Pairnets.Tests.E2E;

/// <summary>
/// A push-channel connection opened the way the apps open it, that keeps every message it hears so the tour can wait
/// for one. Its HTTP requests (negotiate, and every long-polling round) go into the <see cref="TourRecord"/>.
/// </summary>
public sealed class HubProbe : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly TourRecord _record;
    private readonly List<(string Event, object?[] Args)> _heard = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HubProbe(string name, HubConnection hub, TourRecord record)
    {
        Name = name;
        _hub = hub;
        _record = record;
        hub.On<string, string>(SyncHub.ChangedMethod, (device, path) => Heard(SyncHub.ChangedMethod, device, path));
        hub.On<string, int, bool>(SyncHub.PeerBatchMethod, (device, count, active) => Heard(SyncHub.PeerBatchMethod, device, count, active));
        hub.On<string, string>(SyncHub.DeviceRemovedMethod, (id, who) => Heard(SyncHub.DeviceRemovedMethod, id, who));
        hub.On<string, string>(SyncHub.DeviceRenamedMethod, (id, who) => Heard(SyncHub.DeviceRenamedMethod, id, who));
        hub.On<string, string, string>(PairingEndpoints.PairRequestedMethod, (code, who, system) => Heard(PairingEndpoints.PairRequestedMethod, code, who, system));
        hub.On<string, bool>(PairingEndpoints.PairDecidedMethod, (code, approved) => Heard(PairingEndpoints.PairDecidedMethod, code, approved));
        hub.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    /// <summary>Who this is in the tour's messages ("LAPTOP's push channel").</summary>
    public string Name { get; }

    /// <summary>Completes when the server closes the connection (or it is stopped).</summary>
    public Task Closed => _closed.Task;

    public HubConnectionState State => _hub.State;

    public static async Task<HubProbe> ConnectAsync(string name, Uri api, string key, string device, TourRecord record, HttpTransportType transport)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(api, SyncHub.Path.TrimStart('/')), transport, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(key);
                o.Headers[PairnetsHeaders.DeviceId] = Uri.EscapeDataString(device);
                o.Headers[PairnetsHeaders.Client] = PairnetsInfo.ClientDescription;
                o.HttpMessageHandlerFactory = inner => new RecordingHandler(record, inner);
            })
            .Build();
        var probe = new HubProbe(name, hub, record);
        await hub.StartAsync();
        return probe;
    }

    /// <summary>Calls a hub method the way the apps do.</summary>
    public async Task InvokeAsync(string method, params object?[] args)
    {
        await _hub.InvokeCoreAsync(method, args);
        _record.HubCalled(method);
    }

    /// <summary>Waits until a message <paramref name="evt"/> arrives whose arguments pass <paramref name="match"/>.</summary>
    public async Task<object?[]> WaitForAsync(string evt, Func<object?[], bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            Task changed;
            lock (_heard)
            {
                foreach (var (name, args) in _heard)
                {
                    if (name == evt && (match is null || match(args)))
                        return args;
                }
                changed = _changed.Task;
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                throw new TimeoutException($"{Name} never heard {evt} with the expected details. It heard: {Describe()}");
            await Task.WhenAny(changed, Task.Delay(left));
        }
    }

    /// <summary>True when a message <paramref name="evt"/> passing <paramref name="match"/> already arrived.</summary>
    public bool HasHeard(string evt, Func<object?[], bool>? match = null)
    {
        lock (_heard)
            return _heard.Any(h => h.Event == evt && (match is null || match(h.Args)));
    }

    public async Task WaitClosedAsync(string why)
    {
        if (await Task.WhenAny(Closed, Task.Delay(TimeSpan.FromSeconds(20))) != Closed)
            throw new TimeoutException($"{Name} stayed open, but {why}.");
    }

    public Task StopAsync() => _hub.StopAsync();

    private void Heard(string evt, params object?[] args)
    {
        TaskCompletionSource changed;
        lock (_heard)
        {
            _heard.Add((evt, args));
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _record.HubHeard(evt);
        changed.TrySetResult();
    }

    private string Describe()
    {
        lock (_heard)
            return _heard.Count == 0 ? "nothing" : string.Join("; ", _heard.Select(h => $"{h.Event}({string.Join(", ", h.Args)})"));
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
