using System.IO;
using Pairnets.Core.Client;
using Pairnets.Core.Paths;
using Pairnets.Core.Sync;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// The nest's files for the Files page, Clean up and Settings' "Where your files live" in the UI tests of both apps:
/// a few folders and files in memory, and every change written down by name ("rename:Notes/ideas.md:plans.md").
/// </summary>
public sealed class FakeNestFiles : INestFiles
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Size, DateTimeOffset Changed, bool Here)> _files = new(StringComparer.Ordinal);

    public FakeNestFiles()
    {
        var now = DateTimeOffset.Now;
        Put("Documents/report.docx", 2_400_000, now.AddHours(-2));
        Put("Documents/taxes/2025.pdf", 800_000, now.AddDays(-3));
        Put("Photos/beach-day.jpg", 4_200_000, now.AddDays(-1), here: false);
        Put("Videos/wedding-full-day.mp4", 84L << 30, now.AddYears(-6), here: false);
        Put("notes.txt", 2_000, now.AddMinutes(-30));
        Placement["Photos"] = FilePlacement.NestOnly;
    }

    private readonly List<string> _calls = [];

    /// <summary>What the page asked for, in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
                return [.. _calls];
        }
    }

    public Dictionary<string, FilePlacement> Placement { get; } = new(StringComparer.Ordinal);

    public bool NestOffline { get; set; }

    public bool NestFull { get; set; }

    public long? HistoryBytes { get; set; } = 28L << 30;

    /// <summary>What <see cref="OpenAsync"/> answers with as the path to open.</summary>
    public string OpenedPath { get; set; } = Path.Combine(Path.GetTempPath(), "pairnets-opened.txt");

    public void Put(string path, long size, DateTimeOffset changed, bool here = true)
    {
        lock (_gate)
            _files[path] = (size, changed, here);
    }

    public bool Has(string path)
    {
        lock (_gate)
            return _files.ContainsKey(path);
    }

    private void Call(string call)
    {
        lock (_gate)
            _calls.Add(call);
    }

    private List<KeyValuePair<string, (long Size, DateTimeOffset Changed, bool Here)>> All()
    {
        lock (_gate)
            return [.. _files];
    }

    private NestEntry File(string path, (long Size, DateTimeOffset Changed, bool Here) f) =>
        new(path, false, f.Size, f.Changed, "hash-" + path, f.Here ? FileWhere.Here : FileWhere.NestOnly, PlacementOf(path));

    public IReadOnlyList<NestEntry> List(string folder)
    {
        folder = PlacementMap.NormalizeFolder(folder);
        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        var folders = new Dictionary<string, (long Size, int Files, DateTimeOffset Changed)>(StringComparer.Ordinal);
        var files = new List<NestEntry>();
        foreach (var (path, f) in All().Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                files.Add(File(path, f));
                continue;
            }
            var sub = prefix + rest[..slash];
            folders.TryGetValue(sub, out var was);
            folders[sub] = (was.Size + f.Size, was.Files + 1, f.Changed > was.Changed ? f.Changed : was.Changed);
        }
        return
        [
            .. folders.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new NestEntry(f.Key, true, f.Value.Size, f.Value.Changed, null, FileWhere.Here, PlacementOf(f.Key), f.Value.Files)),
            .. files.OrderBy(f => f.Name, StringComparer.Ordinal),
        ];
    }

    public IReadOnlyList<string> Folders(int depth = 1) => All()
        .SelectMany(f =>
        {
            var parts = f.Key.Split('/');
            return Enumerable.Range(1, Math.Min(depth, parts.Length - 1)).Select(i => string.Join('/', parts[..i]));
        })
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<NestEntry> Search(string text, int max = 200) => All()
        .Where(f => text.Trim().Length > 0 && f.Key.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f.Key, StringComparer.Ordinal)
        .Take(max)
        .Select(f => File(f.Key, f.Value))
        .ToList();

    public long BytesHere(string folder)
    {
        folder = PlacementMap.NormalizeFolder(folder);
        return All().Where(f => f.Value.Here && (folder.Length == 0 || f.Key.StartsWith(folder + "/", StringComparison.Ordinal))).Sum(f => f.Value.Size);
    }

    public FilePlacement PlacementOf(string folder)
    {
        lock (_gate)
            return new PlacementMap(Placement).For(folder);
    }

    public void SetPlacement(string folder, FilePlacement placement)
    {
        Call($"place:{folder}:{placement}");
        lock (_gate)
            PlacementMap.Choose(Placement, folder, placement);
    }

    public Task<NestResult> OpenAsync(string path, CancellationToken ct = default)
    {
        Call("open:" + path);
        return Task.FromResult(NestOffline && !Here(path)
            ? NestResult.Fail(NestFiles.OfflineMessage)
            : new NestResult(true, OpenPath: OpenedPath));
    }

    private bool Here(string path)
    {
        lock (_gate)
            return _files.TryGetValue(path, out var f) && f.Here;
    }

    public Task<NestResult> SaveCopyAsync(string path, string destination, CancellationToken ct = default)
    {
        Call($"save:{path}:{destination}");
        return Task.FromResult(NestResult.Done);
    }

    public Task<NestResult> AddAsync(IReadOnlyList<string> localFiles, string folder, CancellationToken ct = default)
    {
        Call($"add:{folder}:{string.Join(",", localFiles.Select(Path.GetFileName))}");
        if (NestFull)
            return Task.FromResult(NestResult.Full);
        foreach (var file in localFiles)
            Put((folder.Length == 0 ? string.Empty : folder + "/") + Path.GetFileName(file), new FileInfo(file).Length, DateTimeOffset.Now);
        return Task.FromResult(NestResult.Done);
    }

    public Task<NestResult> RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        Call($"rename:{path}:{newName}");
        var parent = PathRules.Parent(path);
        return Task.FromResult(Move(path, parent is null ? newName.Trim() : parent + "/" + newName.Trim()));
    }

    public Task<NestResult> MoveAsync(string path, string toFolder, CancellationToken ct = default)
    {
        Call($"move:{path}:{toFolder}");
        return Task.FromResult(Move(path, (toFolder.Length == 0 ? string.Empty : toFolder + "/") + PathRules.FileName(path)));
    }

    private NestResult Move(string from, string to)
    {
        if (NestFull)
            return NestResult.Full;
        lock (_gate)
        {
            foreach (var key in _files.Keys.Where(k => k == from || k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            {
                var f = _files[key];
                _files.Remove(key);
                _files[to + key[from.Length..]] = f;
            }
        }
        return NestResult.Done;
    }

    public Task<NestResult> DeleteAsync(string path, CancellationToken ct = default)
    {
        Call("delete:" + path);
        if (NestFull)
            return Task.FromResult(NestResult.Full);
        Remove(path);
        return Task.FromResult(NestResult.Done);
    }

    private void Remove(string path)
    {
        lock (_gate)
        {
            foreach (var key in _files.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                _files.Remove(key);
        }
    }

    public IReadOnlyList<(FileKind Kind, long Bytes)> KindTotals() => All()
        .GroupBy(f => NestFiles.KindOf(f.Key))
        .Select(g => (g.Key, g.Sum(f => f.Value.Size)))
        .OrderByDescending(t => t.Item2)
        .ToList();

    public IReadOnlyList<NestEntry> Biggest(FileKind? kind = null, int max = 50) => All()
        .Where(f => kind is null || NestFiles.KindOf(f.Key) == kind)
        .OrderByDescending(f => f.Value.Size)
        .Take(max)
        .Select(f => File(f.Key, f.Value))
        .ToList();

    public long BytesOnNest() => All().Sum(f => f.Value.Size);

    public Task<NestResult> DeleteForGoodAsync(string path, string typed, CancellationToken ct = default)
    {
        Call($"purge:{path}:{typed}");
        if (!string.Equals(typed.Trim(), NestFiles.DeleteWord, StringComparison.Ordinal))
            return Task.FromResult(NestResult.Fail($"Type {NestFiles.DeleteWord} to delete it for good."));
        Remove(path);
        return Task.FromResult(NestResult.Done);
    }
}
