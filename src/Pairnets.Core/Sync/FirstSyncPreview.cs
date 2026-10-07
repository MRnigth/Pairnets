using Pairnets.Core.Api;
using Pairnets.Core.Paths;

namespace Pairnets.Core.Sync;

/// <summary>What the first sync of a folder will meet, so the wizard can explain it before starting.</summary>
public sealed record FirstSyncPreview(int LocalFiles, int ServerFiles)
{
    /// <summary>Both sides already have files: they will be merged.</summary>
    public bool IsMerge => LocalFiles > 0 && ServerFiles > 0;

    public const string MergeExplanation =
        "The server already has files and this folder is not empty. Pairnets will merge them:\n\n" +
        "• Files that are identical on both sides are left alone (nothing is transferred).\n" +
        "• Files that exist on only one side are copied to the other.\n" +
        "• Files with the same name but different content are kept twice: the server version keeps the name, " +
        "your local version is saved as \"name (conflict DEVICE date time)\".\n" +
        "• Nothing is deleted on either side.";

    public static async Task<FirstSyncPreview> ComputeAsync(string folder, IgnoreList ignore, IPairnetsApi api, CancellationToken ct)
    {
        var local = 0;
        if (Directory.Exists(folder))
        {
            var root = Path.GetFullPath(folder);
            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                var rel = PathRules.FromOsRelative(Path.GetRelativePath(root, file));
                if (!ignore.IsIgnored(rel))
                    local++;
            }
        }
        var manifest = await api.GetManifestAsync(null, ct).ConfigureAwait(false);
        return new FirstSyncPreview(local, manifest.Entries.Count(e => !e.Deleted));
    }
}
