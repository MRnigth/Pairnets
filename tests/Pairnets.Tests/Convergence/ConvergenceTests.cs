using System.Text;
using Pairnets.Core.Hashing;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Pairnets.Tests.Convergence;

/// <summary>
/// Seeded randomized simulation: two devices create, edit, delete and rename files with sync
/// passes interleaved at random points (so both sides accumulate offline edits). At the end both
/// folders must be byte-identical to each other and to the server, and every content a sync pass
/// ever saw must still exist somewhere: in a folder, in files/, or in history/.
/// </summary>
public class ConvergenceTests(ITestOutputHelper output)
{
    private static readonly string[] Dirs = ["", "d1/", "d1/s/", "d2/"];
    private static readonly string[] Names = ["f0.txt", "f1.txt", "f2.md", "f3.bin"];

    public static IEnumerable<object[]> Seeds()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("PAIRNETS_CONVERGENCE_SEEDS"), out var n) && n > 0 ? n : 30;
        for (var seed = 1; seed <= count; seed++)
            yield return [seed];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task DevicesConvergeAndNothingIsLost(int seed)
    {
        await using var server = await TestServer.StartAsync();
        await using var desktop = new Device("desktop", server);
        await using var laptop = new Device("laptop", server);
        var devices = new[] { desktop, laptop };
        var rnd = new Random(seed);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var log = new StringBuilder();

        async Task Pass(Device d)
        {
            foreach (var h in d.Snapshot().Values)
                observed.Add(h);
            var r = await d.SyncAsync(full: rnd.Next(4) == 0);
            if (r.Outcome == PassOutcome.Blocked && r.BlockReason is BlockReason.MassDelete or BlockReason.FolderEmpty)
            {
                // The user looks at the warning and allows the deletions.
                d.Engine.ApproveBlockedDeletions();
                r = await d.SyncAsync();
            }
            log.AppendLine($"  sync {d.Name}: {r}");
            Assert.True(r.Outcome == PassOutcome.Completed, $"seed {seed}: {d.Name} pass {r}\n{log}");
        }

        var ops = 40 + rnd.Next(40);
        for (var i = 0; i < ops; i++)
        {
            var d = devices[rnd.Next(2)];
            var files = d.Files();
            var roll = rnd.Next(100);
            if (roll < 35)
            {
                var path = Dirs[rnd.Next(Dirs.Length)] + Names[rnd.Next(Names.Length)];
                var content = $"seed {seed} op {i} by {d.Name} {rnd.Next()}" + new string('x', rnd.Next(3) == 0 ? rnd.Next(5000) : rnd.Next(20));
                d.Write(path, content);
                log.AppendLine($"{d.Name} write {path}");
            }
            else if (roll < 50 && files.Count > 0)
            {
                var path = files[rnd.Next(files.Count)];
                d.Delete(path);
                log.AppendLine($"{d.Name} delete {path}");
            }
            else if (roll < 60 && files.Count > 0)
            {
                var from = files[rnd.Next(files.Count)];
                var to = Dirs[rnd.Next(Dirs.Length)] + Names[rnd.Next(Names.Length)];
                if (!d.Exists(to))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(d.Full(to))!);
                    File.Move(d.Full(from), d.Full(to));
                    log.AppendLine($"{d.Name} rename {from} -> {to}");
                }
            }
            else
            {
                await Pass(d);
            }
        }

        // Settle: alternate passes until nothing changes on either side.
        var quiet = false;
        for (var round = 0; round < 10 && !quiet; round++)
        {
            quiet = true;
            foreach (var d in devices)
            {
                foreach (var h in d.Snapshot().Values)
                    observed.Add(h);
                var r = await d.SyncAsync();
                if (r.Outcome == PassOutcome.Blocked && r.BlockReason is BlockReason.MassDelete or BlockReason.FolderEmpty)
                {
                    d.Engine.ApproveBlockedDeletions();
                    r = await d.SyncAsync();
                }
                Assert.True(r.Outcome == PassOutcome.Completed, $"seed {seed}: settle {d.Name} {r}\n{log}");
                if (r.Changes > 0 || r.WantsRetry)
                    quiet = false;
            }
        }
        Assert.True(quiet, $"seed {seed}: devices did not settle\n{log}");

        var a = desktop.Snapshot();
        var b = laptop.Snapshot();
        var serverLive = server.Store.ReadManifest(null).Entries.Where(e => !e.Deleted).ToDictionary(e => e.Path, e => e.Hash!);
        Assert.True(a.OrderBy(k => k.Key).SequenceEqual(b.OrderBy(k => k.Key)), $"seed {seed}: folders differ\n{Dump(a)}\nvs\n{Dump(b)}\n{log}");
        Assert.True(a.OrderBy(k => k.Key).SequenceEqual(serverLive.OrderBy(k => k.Key)), $"seed {seed}: server differs\n{log}");

        var everywhere = new HashSet<string>(a.Values, StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(server.Paths.Files, "*", SearchOption.AllDirectories))
            everywhere.Add(ContentHash.Of(File.ReadAllBytes(f)));
        foreach (var f in Directory.EnumerateFiles(server.Paths.History, "*", SearchOption.AllDirectories))
            everywhere.Add(ContentHash.Of(File.ReadAllBytes(f)));
        var lost = observed.Where(h => !everywhere.Contains(h)).ToList();
        Assert.True(lost.Count == 0, $"seed {seed}: {lost.Count} observed content(s) lost\n{log}");

        Assert.Empty(Directory.EnumerateFiles(server.Paths.Tmp));
        foreach (var d in devices)
        {
            if (Directory.Exists(d.Engine.TempDirectory))
                Assert.Empty(Directory.EnumerateFiles(d.Engine.TempDirectory));
        }
        output.WriteLine($"seed {seed}: {ops} ops, {a.Count} files, {observed.Count} contents observed");
    }

    private static string Dump(Dictionary<string, string> d) =>
        string.Join("\n", d.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value[..8]}"));
}
