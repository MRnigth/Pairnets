using Pairnets.Server;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

[Collection(OnTheirOwn.Name)]
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

        using var stop = new CancellationTokenSource();
        var churning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var churn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                File.WriteAllText(updater.RequestFile, string.Empty);
                churning.TrySetResult();
                File.Delete(updater.RequestFile);
            }
        });
        // 3 s of reads from when the churn really runs (on a busy machine its thread can start late), and on until a
        // read has landed while the file was there (at most 30 s).
        var seen = new HashSet<string>();
        try
        {
            await churning.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow - started < TimeSpan.FromSeconds(3) || (!seen.Contains("requested") && DateTime.UtcNow - started < TimeSpan.FromSeconds(30)))
            {
                var status = updater.GetStatus();
                seen.Add(status.State);
                Assert.False(status.Message?.StartsWith("The updater did not start", StringComparison.Ordinal) ?? false, status.Message);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await churn;
        }
        Assert.Contains("requested", seen); // the reads did land while the file was there
    }

    /// <summary>
    /// update.sh writes its result to status.json and then deletes the request file. The nest used to read status.json
    /// first and the request file second: a read in between got the status from before this update and no request
    /// file, and said "The updater stopped without reporting a result" about an update that had just worked.
    /// </summary>
    [Fact]
    public async Task AnUpdateThatJustFinishedIsNeverOneThatStoppedWithoutAResult()
    {
        using var dir = new TempDir("updater-finish");
        var script = Path.Combine(dir.Path, "update.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        var clock = new AheadClock();
        var updater = new ServerUpdater(new ServerPaths(dir.Path), new SyncOptions { UpdaterScript = script }, clock);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var updates = 0;
        var updaterRuns = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                clock.Ahead(TimeSpan.FromMinutes(2)); // the next "Update server" press, over a minute after the last
                Assert.Equal(ServerUpdater.RequestOutcome.Requested, updater.Request());
                File.SetLastWriteTimeUtc(updater.RequestFile, clock.GetUtcNow().UtcDateTime);
                // update.sh: its result, then the request file goes.
                File.WriteAllText(updater.StatusFile + ".tmp", """{"state":"updated","message":"Updated"}""");
                File.SetLastWriteTimeUtc(updater.StatusFile + ".tmp", clock.GetUtcNow().UtcDateTime.AddSeconds(1));
                ReplaceStatus(updater.StatusFile + ".tmp", updater.StatusFile);
                File.Delete(updater.RequestFile);
                updates++;
            }
        });
        var seen = new HashSet<string>();
        while (!stop.IsCancellationRequested)
        {
            var status = updater.GetStatus();
            seen.Add(status.State);
            Assert.False(status.Message?.StartsWith("The updater stopped without reporting", StringComparison.Ordinal) ?? false, status.Message);
        }
        await updaterRuns;
        Assert.True(updates > 10, $"only {updates} updates ran");
        Assert.Contains("updated", seen);
    }

    /// <summary>update.sh's "mv -f". Windows refuses it while the nest is reading the file: tried again then.</summary>
    private static void ReplaceStatus(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 100)
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>The real time plus a lead the test moves forward (safe to read from another thread).</summary>
    private sealed class AheadClock : TimeProvider
    {
        private long _aheadTicks;

        public void Ahead(TimeSpan by) => Interlocked.Add(ref _aheadTicks, by.Ticks);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _aheadTicks));
    }
}
