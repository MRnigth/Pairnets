using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Ui;

/// <summary>
/// Measurement harness, not a test: the Mac/Linux app uploads many small files with its window open (PAIRNETS_MEMPROBE_WINDOW=1)
/// or hidden, and the managed heap is logged. Runs only with PAIRNETS_MEMPROBE=1 (PAIRNETS_MEMPROBE_FILES, PAIRNETS_MEMPROBE_OUT).
/// </summary>
[Collection(AvaloniaUi.Name)]
public sealed class UploadMemoryUiProbe
{
    [AvaloniaFact]
    public async Task Probe()
    {
        if (Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE") != "1")
            return;
        var count = int.TryParse(Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE_FILES"), out var n) ? n : 50000;
        var window = Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE_WINDOW") == "1";
        var outFile = Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE_OUT") ?? Path.Combine(Path.GetTempPath(), "memprobe-ui.log");
        void Log(string line) => File.AppendAllText(outFile, line + Environment.NewLine);

        await using var app = await AppUnderTest.StartAsync();
        Directory.CreateDirectory(app.Folder);
        for (var d = 0; d < 500; d++)
        {
            var dir = Path.Combine(app.Folder, "dir" + d);
            Directory.CreateDirectory(dir);
            for (var f = 0; f < count / 500; f++)
                File.WriteAllText(Path.Combine(dir, $"file{f}.txt"), $"content {d}/{f} " + new string('x', 150));
        }
        if (window)
            app.App.ShowMainWindow();

        async Task Pump(TimeSpan span)
        {
            var until = DateTime.UtcNow + span;
            while (DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(50);
            }
        }

        string Sample(string tag)
        {
            var s = app.App.Session?.Status;
            return $"{tag} t={DateTime.Now:HH:mm:ss} raw={GC.GetTotalMemory(false) / 1048576}MB ws={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB files={s?.FilesDone}/{s?.FilesTotal} status={s?.Status}";
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMinutes(25))
        {
            var done = (await app.NestFilesAsync()).Count;
            Log(Sample($"up done={done}"));
            if (done >= count)
                break;
            await Pump(TimeSpan.FromSeconds(5));
        }
        await Pump(TimeSpan.FromSeconds(10));
        Log(Sample("end-of-upload"));
        for (var i = 0; i < 24; i++)
        {
            await Pump(TimeSpan.FromSeconds(5));
            Log(Sample($"idle{i}"));
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (Environment.GetEnvironmentVariable("PAIRNETS_MEMPROBE_HOLD") == "1")
        {
            Log("holding for a heap dump");
            await Pump(TimeSpan.FromMinutes(4));
        }
        Log($"after-full-gc live={GC.GetTotalMemory(true) / 1048576}MB ws={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB");
    }
}
