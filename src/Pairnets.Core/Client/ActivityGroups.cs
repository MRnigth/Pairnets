namespace Pairnets.Core.Client;

/// <summary>
/// Several files of one folder that synced the same way at about the same time ("Photos/summer · 18 files
/// uploaded"): one row in the activity lists, which opens to list the files.
/// </summary>
public sealed record ActivityFolder(string Folder, ActivityKind Kind, DateTimeOffset Time, IReadOnlyList<ActivityItem> Items)
{
    public string Primary => Folder;

    /// <summary>"18 files uploaded".</summary>
    public string Secondary => $"{Items.Count} files {Verb}";

    public string WhenText => Format.RelativeTime(Time, DateTimeOffset.Now);

    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private string Verb => Kind switch
    {
        ActivityKind.Uploaded => "uploaded",
        ActivityKind.Downloaded => "downloaded",
        ActivityKind.DeletedHere => "deleted here",
        ActivityKind.DeletedOnServer => "deleted on the server",
        _ => "changed",
    };
}

/// <summary>Folds runs of same-folder activity into <see cref="ActivityFolder"/> rows.</summary>
public static class ActivityGroups
{
    /// <summary>A run needs at least this many files to become one folder row.</summary>
    public const int MinFiles = 3;

    /// <summary>Files further apart than this are not one batch.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The items (newest first) with runs of at least <see cref="MinFiles"/> uploads, downloads or deletions in one
    /// folder, each within <see cref="Gap"/> of the next, folded into one <see cref="ActivityFolder"/>. Everything else
    /// stays an <see cref="ActivityItem"/>.
    /// </summary>
    public static IReadOnlyList<object> Group(IReadOnlyList<ActivityItem> items)
    {
        var rows = new List<object>(items.Count);
        var i = 0;
        while (i < items.Count)
        {
            var first = items[i];
            var folder = Groupable(first) ? Paths.PathRules.Parent(first.Path!) : null;
            var end = i + 1;
            if (folder is not null)
            {
                while (end < items.Count
                       && items[end].Kind == first.Kind
                       && items[end].Path is { } path
                       && Paths.PathRules.Parent(path) == folder
                       && (items[end - 1].Time - items[end].Time).Duration() <= Gap)
                    end++;
            }
            if (folder is not null && end - i >= MinFiles)
            {
                rows.Add(new ActivityFolder(folder, first.Kind, first.Time, items.Skip(i).Take(end - i).ToList()));
                i = end;
            }
            else
            {
                rows.Add(first);
                i++;
            }
        }
        return rows;
    }

    /// <summary>The first <paramref name="count"/> grouped rows (the overview and the tray panel).</summary>
    public static IReadOnlyList<object> Recent(IReadOnlyList<ActivityItem> items, int count) => Group(items).Take(count).ToList();

    private static bool Groupable(ActivityItem item) =>
        item.Path is not null
        && item.Kind is ActivityKind.Uploaded or ActivityKind.Downloaded or ActivityKind.DeletedHere or ActivityKind.DeletedOnServer;
}
