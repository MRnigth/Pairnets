using Pairnets.Server.Storage;

namespace Pairnets.Server.Services;

/// <summary>Daily purge of history versions older than the retention period (keeping the minimum count).</summary>
public sealed class HistoryPurgeService(SyncStore store, SyncOptions options, ILogger<HistoryPurgeService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.PurgeInitialDelay, stoppingToken);
            using var timer = new PeriodicTimer(options.PurgeInterval);
            do
            {
                try
                {
                    await store.PurgeHistoryAsync(TimeSpan.FromDays(options.HistoryRetentionDays), options.HistoryMinVersions, dryRun: false, stoppingToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.LogError(ex, "History purge failed; will retry at the next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
