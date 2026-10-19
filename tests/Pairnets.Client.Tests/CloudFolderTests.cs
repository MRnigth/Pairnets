using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Client.Platform.CloudFiles;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Client.Tests;

/// <summary>
/// Runs only where <c>PAIRNETS_CLOUD_TESTS=1</c> (GitHub's throwaway Windows computers): a Windows cloud folder shows in
/// Explorer's left side while it exists, so these tests never run on a person's PC.
/// </summary>
public sealed class CloudFactAttribute : FactAttribute
{
    public CloudFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PAIRNETS_CLOUD_TESTS") != "1")
            Skip = "Windows cloud folder tests run only on CI computers (PAIRNETS_CLOUD_TESTS=1).";
    }
}

/// <summary>
/// The Pairnets folder as a real Windows cloud folder: marking files synced, files left on the nest and fetched when
/// opened, "Free up space", and turning it back into a plain folder with every file intact.
/// </summary>
[Collection("Windows cloud folder")] // one cloud folder per Windows user at a time
public sealed class CloudFolderTests : IDisposable
{
    private readonly TempDir _dir = new("cloud");

    public void Dispose()
    {
        WindowsCloudFolder.ForgetAll();
        _dir.Dispose();
    }

    private string Root()
    {
        var root = _dir.Combine("Pairnets");
        Directory.CreateDirectory(root);
        return root;
    }

    private static WindowsCloudFolder Start(string root, CloudFolderMode mode)
    {
        var cloud = WindowsCloudFolder.Start(root, mode, Path.Combine(Environment.SystemDirectory, "imageres.dll"), NullLogger.Instance, out var problem);
        Assert.True(cloud is not null, "Windows did not take the folder: " + problem);
        return cloud!;
    }

    private static (bool Placeholder, bool InSync, bool Online) State(string path)
    {
        Assert.True(CloudPlaceholders.TryGetAttributesAndTag(path, out var attributes, out var tag), path + " is missing");
        var state = CfApi.CfGetPlaceholderStateFromAttributeTag((int)attributes, (int)tag);
        return ((state & CfApi.PlaceholderStatePlaceholder) != 0, (state & CfApi.PlaceholderStateInSync) != 0, CloudPlaceholders.IsContentOnline(attributes));
    }

    /// <summary>Reads a file from another program, the way an app opening it does (Pairnets itself never fetches by reading).</summary>
    private static string ReadElsewhere(string path)
    {
        var copy = path + ".read";
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c copy /y \"{path}\" \"{copy}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            Assert.True(p.WaitForExit(60_000), "opening the file took over a minute");
        var text = File.Exists(copy) ? File.ReadAllText(copy) : "(could not be read)";
        File.Delete(copy);
        return text;
    }

    [CloudFact]
    public async Task KeepAllHere_MarksFilesWhereTheyAre_AndLeavingGivesPlainFilesBack()
    {
        var root = Root();
        var file = Path.Combine(root, "Docs", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "my report");

        using (var cloud = Start(root, CloudFolderMode.KeepAllHere))
        {
            Assert.Contains(WindowsCloudFolder.Registered(), r => string.Equals(r.Path, root, StringComparison.OrdinalIgnoreCase));
            cloud.MarkSynced(file, "hash-1");
            Assert.Equal((true, true, false), State(file));
            Assert.False(cloud.IsCloudOnly(file));
            Assert.False(cloud.KeepOnNest(file)); // everything stays here in this mode

            File.AppendAllText(file, ", changed"); // a change here takes the tick away (Windows does that by itself)
            Assert.False(State(file).InSync);

            Assert.Null(await cloud.LeaveAsync(CancellationToken.None));
        }
        Assert.Empty(WindowsCloudFolder.Registered());
        Assert.False(State(file).Placeholder);
        Assert.Equal("my report, changed", File.ReadAllText(file));
    }

    [CloudFact]
    public async Task OnDemand_AFileStaysOnTheNestUntilOpened_ThenFreeUpSpaceLeavesItThereAgain()
    {
        var root = Root();
        var content = "content from the nest " + new string('n', 3 * 1024 * 1024); // more than one 1 MB piece
        var fetched = new List<(string Path, string Hash)>();
        using var cloud = Start(root, CloudFolderMode.OnDemand);
        cloud.Fetch = async (rel, hash, stream, progress, ct) =>
        {
            lock (fetched)
                fetched.Add((rel, hash));
            var bytes = Encoding.UTF8.GetBytes(content);
            for (var at = 0; at < bytes.Length; at += 100_000)
            {
                await stream.WriteAsync(bytes.AsMemory(at, Math.Min(100_000, bytes.Length - at)), ct);
                progress(at);
            }
        };
        var file = Path.Combine(root, "Photos", "Beach.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(cloud.KeepOnNest(file));
        Assert.True(cloud.PlaceOnline(file, "v1", Encoding.UTF8.GetByteCount(content), when));

        Assert.Equal((true, true, true), State(file));
        Assert.True(cloud.IsCloudOnly(file));
        Assert.Equal(Encoding.UTF8.GetByteCount(content), new FileInfo(file).Length); // it shows its real size
        Assert.Equal(when, new FileInfo(file).LastWriteTimeUtc);
        Assert.Empty(fetched);

        Assert.Equal(content, ReadElsewhere(file)); // opening it fetches it
        Assert.Equal([("Photos/Beach.txt", "v1")], fetched);
        Assert.False(cloud.IsCloudOnly(file));

        // "Free up space": what Explorer's menu sets on the file; Pairnets leaves the content on the nest again.
        using (var p = Process.Start(new ProcessStartInfo("attrib.exe", $"+U -P \"{file}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit(30_000);
        var freed = SpinWait.SpinUntil(() => cloud.IsCloudOnly(file), TimeSpan.FromSeconds(20));
        Assert.True(freed, "Free up space did not leave the file on the nest");

        // A newer version on the nest: the placeholder takes its details, the content still stays there.
        Assert.True(cloud.PlaceOnline(file, "v2", 10, when.AddDays(1)));
        Assert.Equal(10, new FileInfo(file).Length);

        // Leaving fetches everything first, so no file is left behind as an empty shortcut.
        content = "version 2!";
        Assert.Null(await cloud.LeaveAsync(CancellationToken.None));
        Assert.Equal("version 2!", File.ReadAllText(file));
        Assert.False(State(file).Placeholder);
    }

    [CloudFact]
    public void AFetchThatFails_GivesTheOpenerAnError_AndTheFileStaysOnTheNest()
    {
        var root = Root();
        using var cloud = Start(root, CloudFolderMode.OnDemand);
        cloud.Fetch = (_, _, _, _, _) => throw new IOException("the nest cannot be reached");
        var file = Path.Combine(root, "far away.txt");
        Assert.True(cloud.PlaceOnline(file, "v1", 1000, DateTime.UtcNow));

        Assert.Equal("(could not be read)", ReadElsewhere(file));
        Assert.True(cloud.IsCloudOnly(file));
    }

    [CloudFact]
    public async Task TheScanner_SeesPlaceholdersAsFiles_AndNeverReadsOneOnTheNest()
    {
        var root = Root();
        using var cloud = Start(root, CloudFolderMode.OnDemand);
        var here = Path.Combine(root, "here.txt");
        File.WriteAllText(here, "local");
        cloud.MarkSynced(here, "h");
        Assert.True(cloud.PlaceOnline(Path.Combine(root, "there.txt"), "t", 5000, DateTime.UtcNow.AddDays(-1)));
        using var state = new Pairnets.Core.State.StateDb(_dir.Combine("state.db"));
        var scanner = new LocalScanner(root, new Pairnets.Core.Paths.IgnoreList(), state, TimeProvider.System, NullLogger.Instance)
        {
            CloudFiles = cloud,
            StabilityWindow = TimeSpan.Zero,
        };

        var scan = await scanner.ScanAsync(CancellationToken.None);

        Assert.True(scan.Files["here.txt"].Stable);
        Assert.False(scan.Files["there.txt"].Stable); // unknown to the notes and only on the nest: not read, not deleted
        Assert.True(cloud.IsCloudOnly(Path.Combine(root, "there.txt")));

        // A plain folder's scanner keeps skipping cloud placeholders (as before), so nothing changes when the switch is off.
        var plain = new LocalScanner(root, new Pairnets.Core.Paths.IgnoreList(), state, TimeProvider.System, NullLogger.Instance) { StabilityWindow = TimeSpan.Zero };
        Assert.DoesNotContain("there.txt", (await plain.ScanAsync(CancellationToken.None)).Files.Keys);
    }

    [CloudFact]
    public void AFolderInsideAnotherCloudFolder_StaysPlain()
    {
        var root = Root();
        using var cloud = Start(root, CloudFolderMode.KeepAllHere);
        var inner = Path.Combine(root, "inner");
        Directory.CreateDirectory(inner);
        // (Pairnets' own cloud folder does not count: the same folder may be started again)
        Assert.Null(WindowsCloudFolder.WhyNot(root));
        Assert.NotNull(WindowsCloudFolder.WhyNot(@"\\server\share\Pairnets"));
    }
}
