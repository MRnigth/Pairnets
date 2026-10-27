using System.Collections.Concurrent;

namespace Pairnets.Core.Paths;

/// <summary>
/// The folders a project's tools make again by themselves (Unity's Library, Unreal's Intermediate, node_modules), which
/// Pairnets skips: thousands of files that change all the time and are no use on another computer. Only inside a
/// project recognised by its own file, so a folder that merely has the same name ("Library" with your books) still syncs.
/// <list type="bullet">
/// <item>Unity (ProjectSettings/ProjectVersion.txt): Library, Temp, Logs, UserSettings.</item>
/// <item>Unreal (a *.uproject file): Binaries, Intermediate, Saved, DerivedDataCache.</item>
/// <item>Node (package.json): node_modules.</item>
/// </list>
/// Projects are learned from the files Pairnets sees (this computer's, the nest's and the last-synced ones), so every pass
/// skips the same folders when it looks at the folder and when it compares with the nest. Skipped folders are like
/// ignored ones: nothing in them is uploaded, downloaded or deleted.
/// </summary>
public sealed class BuildFolders
{
    private static readonly string[] Unity = ["Library", "Temp", "Logs", "UserSettings"];
    private static readonly string[] Unreal = ["Binaries", "Intermediate", "Saved", "DerivedDataCache"];
    private static readonly string[] Node = ["node_modules"];

    /// <summary>Project folder ("" for the top) → the folder names it makes again. Keys are lower case.</summary>
    private readonly ConcurrentDictionary<string, string[]> _projects = new(StringComparer.Ordinal);

    /// <summary>True once a project is known (a quick check before the path is taken apart).</summary>
    public bool Any => !_projects.IsEmpty;

    /// <summary>Notes the project <paramref name="filePath"/> (a file's sync path) shows, if it is a project's own file.</summary>
    public void Learn(string filePath)
    {
        var name = PathRules.FileName(filePath);
        var parent = PathRules.Parent(filePath) ?? string.Empty;
        if (name.Equals("ProjectVersion.txt", StringComparison.OrdinalIgnoreCase)
            && PathRules.FileName(parent).Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase) && parent.Length > 0)
            Add(PathRules.Parent(parent) ?? string.Empty, Unity);
        else if (name.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) && name.Length > ".uproject".Length)
            Add(parent, Unreal);
        else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            Add(parent, Node);
    }

    /// <summary>True when the folder at <paramref name="folderPath"/> is one its project makes again.</summary>
    public bool IsBuildFolder(string folderPath)
    {
        if (_projects.IsEmpty)
            return false;
        var parent = PathRules.Parent(folderPath) ?? string.Empty;
        return _projects.TryGetValue(parent.ToLowerInvariant(), out var names)
            && names.Contains(PathRules.FileName(folderPath), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Forgets every project (a pass learns them again, so a removed project stops counting).</summary>
    public void Clear() => _projects.Clear();

    private void Add(string project, string[] names) =>
        _projects.AddOrUpdate(project.ToLowerInvariant(), names, (_, had) => had.Union(names, StringComparer.OrdinalIgnoreCase).ToArray());
}
