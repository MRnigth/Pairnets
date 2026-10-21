using Pairnets.Core.Api;
using Pairnets.Core.Client;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Services;

/// <summary>
/// Runs every link of this nest (<see cref="LinkStore"/>) now and then, and at once after one is added or changed
/// (<see cref="Kick"/>). Each link reaches the other nest through the Pairnets service (its relay address), with this
/// nest's own key there, like a computer would. A nest that isn't linked to the service has no links and does nothing.
/// </summary>
public sealed class NestLinkWorker(LinkStore links, LinkSync sync, SyncOptions options, ILogger<NestLinkWorker> log) : BackgroundService
{
    private readonly SemaphoreSlim _kick = new(0, 1);

    /// <summary>Makes the worker run a pass soon instead of waiting for <see cref="SyncOptions.LinkInterval"/>.</summary>
    public void Kick()
    {
        try
        {
            _kick.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is due already.
        }
    }

    /// <summary>How the worker reaches the other nest; tests point it at a test nest instead of the service.</summary>
    public Func<NestLink, IPairnetsApi> Connect { get; set; } = link =>
        new PairnetsApiClient(Relay.AddressOf(link.RemoteNestId, new Uri(options.RelayServiceUrl)), link.Key!, link.DeviceId,
            serviceUrl: new Uri(options.RelayServiceUrl));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAny(Task.Delay(options.LinkInitialDelay, stoppingToken), _kick.WaitAsync(stoppingToken)).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunAllAsync(stoppingToken).ConfigureAwait(false);
                await Task.WhenAny(Task.Delay(options.LinkInterval, stoppingToken), _kick.WaitAsync(stoppingToken)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass of every active link (also what tests call).</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        foreach (var link in links.List().Where(l => l.IsActive))
        {
            var api = Connect(link);
            try
            {
                var result = await sync.RunAsync(link, api, ct).ConfigureAwait(false);
                if (result.Pulled + result.Pushed > 0)
                    log.LogInformation("Link {Link}: {Pulled} in, {Pushed} out", link.Id, result.Pulled, result.Pushed);
                else if (result.Error is not null)
                    log.LogInformation("Link {Link}: {State}: {Error}", link.Id, result.State, result.Error);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One link's problem never stops the others or the nest.
                log.LogWarning(ex, "Link {Link} failed", link.Id);
                links.Record(link.Id, LinkState.Problem, "Something went wrong. Pairnets tries again in a minute.");
            }
            finally
            {
                (api as IDisposable)?.Dispose();
            }
        }
    }
}
