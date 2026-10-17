using Pairnets.Server;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public class ServerUpdaterStatusTests
{
    /// <summary>
    /// update.sh deletes its request file when it ends. Read just then, the file's time used to come back as the year
    /// 1601 (what .NET says for a file that is gone), so the nest reported "The updater did not start within 45 seconds"
    /// for an update that had only just finished. Seen as a ClientSessionTests failure now and then.
    /// </summary>
    [Fact]
    public async Task ARequestFileDeletedWhileTheStatusIsReadIsNeverAnUpdaterThatDidNotStart()
    {
        using var dir = new TempDir("updater-status");
        var script = Path.Combine(dir.Path, "update.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        var updater = new ServerUpdater(new ServerPaths(dir.Path), new SyncOptions { UpdaterScript = script });
        Directory.CreateDirectory(updater.UpdateDir);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                File.WriteAllText(updater.RequestFile, string.Empty);
                File.Delete(updater.RequestFile);
            }
        });
        var seen = new HashSet<string>();
        while (!stop.IsCancellationRequested)
        {
            var status = updater.GetStatus();
            seen.Add(status.State);
            Assert.False(status.Message?.StartsWith("The updater did not start", StringComparison.Ordinal) ?? false, status.Message);
        }
        await churn;
        Assert.Contains("requested", seen); // the reads did land while the file was there
    }
}
