using System.Diagnostics;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Measurement harness, not a test: uploads many small files through a ClientSession and logs the managed heap while it
/// runs and while idle. Runs only with PAIRNETS_MEMPROBE=1 (PAIRNETS_MEMPROBE_FILES = count, default 50000;
/// PAIRNETS_MEMPROBE_OUT = log file).
/// </summary>
public class UploadMemoryProbe
{
    [Fact]
    public async Task Probe()
    {
        if (Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE") != "1")
            return;
        var count = int.TryParse(Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE_FILES"), out var n) ? n : 50000;
        var outFile = Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE_OUT") ?? Path.Combine(Path.GetTempPath(), "memprobe.log");
        void Log(string line)
        {
            File.AppendAllText(outFile, line + Environment.NewLine);
        }

        await using var server = await TestServer.StartAsync();
        using var folder = new TempDir("probe");
        using var stateBase = new TempDir("probe-state");
        for (var d = 0; d < 500; d++)
        {
            var dir = Path.Combine(folder.Path, "dir" + d);
            Directory.CreateDirectory(dir);
            for (var f = 0; f < count / 500; f++)
                File.WriteAllText(Path.Combine(dir, $"file{f}.txt"), $"content {d}/{f} " + new string('x', 150));
        }

        await using var session = ClientSession.Start(new ClientSettings
        {
            ServerUrl = server.Url.ToString(),
            ProtectedToken = "unused-by-session",
            Folder = folder.Path,
            DeviceName = "probe",
            FirstRunCompleted = true,
        }, server.Token, new PermanentDeleteTrash(), stateBaseDir: stateBase.Path);

        // Like the windows: they keep the latest snapshot and read the warnings.
        StatusSnapshot? latest = null;
        session.StatusChanged += s => latest = s;

        string Sample(string tag)
        {
            var gen = GC.GetGCMemoryInfo();
            var raw = GC.GetTotalMemory(false) / 1048576;
            return $"{tag} t={DateTime.Now:HH:mm:ss} raw={raw}MB ws={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB gen0-2/loh/poh={string.Join("/", Enumerable.Range(0, gen.GenerationInfo.Length).Select(k => gen.GenerationInfo[k].SizeAfterBytes / 1048576))}MB " +
                $"files={latest?.FilesDone}/{latest?.FilesTotal} batch={latest?.Batch.Count} warnings={session.Warnings().Count} act={session.Activity.Items.Count} status={latest?.Status}";
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(40))
        {
            var done = server.Store.ReadManifest(null).Entries.Count(e => !e.Deleted);
            Log(Sample($"up done={done}"));
            if (done >= count)
                break;
            await Task.Delay(5000);
        }
        for (var i = 0; i < 30 && session.Status.Status == RunnerStatus.Syncing; i++)
            await Task.Delay(2000);
        Log(Sample("end-of-upload"));
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(5000);
            Log(Sample($"idle{i}"));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Log($"after-full-gc live={GC.GetTotalMemory(true) / 1048576}MB ws={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB");
        if (Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE_HOLD") == "1")
            await Task.Delay(TimeSpan.FromMinutes(10)); // time to take a dotnet-gcdump of this process
    }
}
