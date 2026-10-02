using Tether.Core.Paths;

namespace Tether.Server.Storage;

/// <summary>
/// In-memory index of live files and folders by lower-cased path. Windows clients cannot hold two
/// names that differ only in case, nor a file and a folder with the same name, so the server
/// refuses to create them. Not thread-safe: used under the store's write lock.
/// </summary>
public sealed class CaseIndex
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Actual, int Count)> _dirs = new(StringComparer.Ordinal);

    public int FileCount => _files.Count;

    public void Add(string path)
    {
        var key = PathRules.CaseKey(path);
        if (_files.ContainsKey(key))
            return;
        _files[key] = path;
        foreach (var dir in PathRules.DirectoryPrefixes(path))
        {
            var dirKey = PathRules.CaseKey(dir);
            _dirs[dirKey] = _dirs.TryGetValue(dirKey, out var d) ? (d.Actual, d.Count + 1) : (dir, 1);
        }
    }

    public void Remove(string path)
    {
        var key = PathRules.CaseKey(path);
        if (!_files.TryGetValue(key, out var actual) || actual != path)
            return;
        _files.Remove(key);
        foreach (var dir in PathRules.DirectoryPrefixes(path))
        {
            var dirKey = PathRules.CaseKey(dir);
            if (!_dirs.TryGetValue(dirKey, out var d))
                continue;
            if (d.Count <= 1)
                _dirs.Remove(dirKey);
            else
                _dirs[dirKey] = (d.Actual, d.Count - 1);
        }
    }

    /// <summary>Returns a human-readable reason when <paramref name="path"/> cannot coexist with live entries.</summary>
    public string? FindCollision(string path)
    {
        var key = PathRules.CaseKey(path);
        if (_files.TryGetValue(key, out var existing) && existing != path)
            return $"'{path}' differs only in letter case from the existing file '{existing}'.";
        if (_dirs.TryGetValue(key, out var folder))
            return $"A folder '{folder.Actual}' already exists where the file '{path}' should be.";
        foreach (var dir in PathRules.DirectoryPrefixes(path))
        {
            var dirKey = PathRules.CaseKey(dir);
            if (_files.TryGetValue(dirKey, out var file))
                return $"A file '{file}' already exists where the folder '{dir}' should be.";
            if (_dirs.TryGetValue(dirKey, out var d) && d.Actual != dir)
                return $"Folder '{dir}' differs only in letter case from the existing folder '{d.Actual}'.";
        }
        return null;
    }
}
