using Pairnets.Core.Paths;

namespace Pairnets.Core.Sync;

/// <summary>Where the files of one folder live on this computer ("Where your files live" in Settings).</summary>
public enum FilePlacement
{
    /// <summary>Every file is on this computer, as always.</summary>
    OnThisPc,

    /// <summary>Windows only: files show in Explorer but stay on the nest until opened (Files On-Demand).</summary>
    OnDemand,

    /// <summary>Nothing is on this computer: the files are on the nest only, and opened from the Files page.</summary>
    NestOnly,
}

/// <summary>
/// The placement chosen for folders of the Pairnets folder on this computer (never synced to other computers). A path
/// takes the choice of the nearest folder above it that has one; with none, it is <see cref="FilePlacement.OnThisPc"/>.
/// The empty folder ("") stands for the whole Pairnets folder. Letter case does not matter, as on the nest.
/// </summary>
public sealed class PlacementMap
{
    private readonly Dictionary<string, FilePlacement> _byKey;

    /// <param name="folders">Folder (sync path, "/" between names) → placement.</param>
    /// <param name="onDemandWorks">False off Windows or where Windows can't make a cloud folder: On-Demand then means on this PC.</param>
    public PlacementMap(IEnumerable<KeyValuePair<string, FilePlacement>> folders, bool onDemandWorks = true)
    {
        _byKey = new Dictionary<string, FilePlacement>(StringComparer.Ordinal);
        foreach (var (folder, placement) in folders)
        {
            var mode = placement == FilePlacement.OnDemand && !onDemandWorks ? FilePlacement.OnThisPc : placement;
            _byKey[PathRules.CaseKey(NormalizeFolder(folder))] = mode;
        }
    }

    /// <summary>Every file on this computer.</summary>
    public static PlacementMap Everywhere { get; } = new([]);

    /// <summary>True when some folder is not fully on this computer.</summary>
    public bool AnyAway => _byKey.Values.Any(p => p != FilePlacement.OnThisPc);

    /// <summary>True when some folder uses <paramref name="placement"/>.</summary>
    public bool Uses(FilePlacement placement) => _byKey.ContainsValue(placement);

    /// <summary>"Photos\\2024\\" or "/Photos/2024" → "Photos/2024"; the whole Pairnets folder is "".</summary>
    public static string NormalizeFolder(string folder) => folder.Replace('\\', '/').Trim().Trim('/');

    /// <summary>
    /// Writes the choice for <paramref name="folder"/> into <paramref name="placement"/> (the settings' list). A choice
    /// that is the same as the folder above it is no choice of its own, so it is left out.
    /// </summary>
    public static void Choose(Dictionary<string, FilePlacement> placement, string folder, FilePlacement choice)
    {
        folder = NormalizeFolder(folder);
        var above = folder.Length == 0 ? FilePlacement.OnThisPc
            : new PlacementMap(placement.Where(p => NormalizeFolder(p.Key) != folder)).For(folder);
        foreach (var key in placement.Keys.Where(k => PathRules.CaseKey(NormalizeFolder(k)) == PathRules.CaseKey(folder)).ToList())
            placement.Remove(key);
        if (choice != above)
            placement[folder] = choice;
    }

    /// <summary>Where the file or folder at <paramref name="path"/> lives.</summary>
    public FilePlacement For(string path)
    {
        if (_byKey.Count == 0)
            return FilePlacement.OnThisPc;
        for (string? at = NormalizeFolder(path); at is not null; at = at.Length == 0 ? null : PathRules.Parent(at) ?? "")
        {
            if (_byKey.TryGetValue(PathRules.CaseKey(at), out var placement))
                return placement;
        }
        return FilePlacement.OnThisPc;
    }
}
