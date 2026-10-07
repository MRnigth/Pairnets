namespace Pairnets.Server.Services;

/// <summary>
/// A removed computer must lose its live push connection too, not only its HTTP access. Removals made by this
/// process close the connection at once; this check catches the others (<c>tether-server devices remove</c> runs
/// in another process, and turning the shared token off only changes a setting).
/// </summary>
public sealed class ConnectionGuardService(DeviceRegistry devices, SyncOptions options, ILogger<ConnectionGuardService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.ConnectionCheckInterval > TimeSpan.Zero ? options.ConnectionCheckInterval : TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (devices.CloseDisallowed() is > 0 and var closed)
                        log.LogInformation("Closed {Count} push connection(s) of computers that are no longer allowed in", closed);
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
                {
                    log.LogWarning(ex, "Could not check the live connections; will retry");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
