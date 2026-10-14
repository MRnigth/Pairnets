using System.Globalization;

namespace Pairnets.Core.Client;

/// <summary>The filter buttons above the Activity list.</summary>
public enum ActivityFilter
{
    All,
    Uploaded,
    Downloaded,
    Deleted,
    Conflicts,
}

/// <summary>How many items each filter button counts ("All 55", "Uploaded 40").</summary>
public sealed record ActivityCounts(int All, int Uploaded, int Downloaded, int Deleted, int Conflicts)
{
    public int Of(ActivityFilter filter) => filter switch
    {
        ActivityFilter.Uploaded => Uploaded,
        ActivityFilter.Downloaded => Downloaded,
        ActivityFilter.Deleted => Deleted,
        ActivityFilter.Conflicts => Conflicts,
        _ => All,
    };
}

/// <summary>
/// What the Activity page draws: the filter counts, and the rows (day headings with their change counts, single files,
/// folder rows) for one filter and search text. Both apps use this, so they filter the same way.
/// </summary>
public static class ActivityView
{
    public static readonly IReadOnlyList<ActivityFilter> Filters =
        [ActivityFilter.All, ActivityFilter.Uploaded, ActivityFilter.Downloaded, ActivityFilter.Deleted, ActivityFilter.Conflicts];

    public static string Label(ActivityFilter filter) => filter switch
    {
        ActivityFilter.Uploaded => "Uploaded",
        ActivityFilter.Downloaded => "Downloaded",
        ActivityFilter.Deleted => "Deleted",
        ActivityFilter.Conflicts => "Conflicts",
        _ => "All",
    };

    public static bool Matches(ActivityItem item, ActivityFilter filter) => filter switch
    {
        ActivityFilter.Uploaded => item.Kind == ActivityKind.Uploaded,
        ActivityFilter.Downloaded => item.Kind == ActivityKind.Downloaded,
        ActivityFilter.Deleted => item.Kind is ActivityKind.DeletedHere or ActivityKind.DeletedOnServer,
        ActivityFilter.Conflicts => item.Kind == ActivityKind.Conflict,
        _ => true,
    };

    /// <summary>The search text matches the file's path (so a folder name finds its files) or the message.</summary>
    public static bool Matches(ActivityItem item, string? search)
    {
        var term = search?.Trim();
        return string.IsNullOrEmpty(term)
            || (item.Path ?? item.Text).Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.Text.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The counts on the filter buttons. They follow the search text, so "Uploaded 3" is what a click would show.</summary>
    public static ActivityCounts Counts(IReadOnlyList<ActivityItem> items, string? search = null)
    {
        var found = items.Where(i => Matches(i, search)).ToList();
        return new ActivityCounts(
            found.Count,
            found.Count(i => Matches(i, ActivityFilter.Uploaded)),
            found.Count(i => Matches(i, ActivityFilter.Downloaded)),
            found.Count(i => Matches(i, ActivityFilter.Deleted)),
            found.Count(i => Matches(i, ActivityFilter.Conflicts)));
    }

    /// <summary>The rows for the list: the items that pass the filter and search, grouped by day and folder.</summary>
    public static IReadOnlyList<object> Rows(IReadOnlyList<ActivityItem> items, ActivityFilter filter, string? search, DateTimeOffset now) =>
        ActivityDays.GroupedRows(items.Where(i => Matches(i, filter) && Matches(i, search)).ToList(), now);

    /// <summary>What an empty list says: nothing synced yet, or nothing matches the filter or search.</summary>
    public static string EmptyText(IReadOnlyList<ActivityItem> items, ActivityFilter filter, string? search)
    {
        if (items.Count == 0)
            return "Nothing has synced yet";
        return !string.IsNullOrWhiteSpace(search)
            ? "Nothing matches \"" + search.Trim() + "\""
            : filter == ActivityFilter.All ? "Nothing has synced yet" : $"No {Label(filter).ToLower(CultureInfo.InvariantCulture)} files to show";
    }

    /// <summary>Conflict rows get a "Review" button (it opens Needs you).</summary>
    public static bool NeedsReview(ActivityItem item) => item.Kind == ActivityKind.Conflict;
}
