namespace Pairnets.Core.Client;

public enum ActivityKind
{
    Uploaded,
    Downloaded,
    DeletedHere,
    DeletedOnServer,
    Conflict,
    Warning,
    Blocked,
    Offline,
    Error,
    Info,
}

/// <summary>One line in the app's activity list.</summary>
public sealed record ActivityItem(DateTimeOffset Time, ActivityKind Kind, string? Path, string Text)
{
    /// <summary>A short symbol for list views (no image assets needed).</summary>
    public string Symbol => Kind switch
    {
        ActivityKind.Uploaded => "↑",
        ActivityKind.Downloaded => "↓",
        ActivityKind.DeletedHere or ActivityKind.DeletedOnServer => "✕",
        ActivityKind.Conflict => "⚠",
        ActivityKind.Warning => "!",
        ActivityKind.Blocked => "⏸",
        ActivityKind.Offline => "○",
        ActivityKind.Error => "✖",
        _ => "•",
    };

    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Short label for the kind of event.</summary>
    public string Title => Kind switch
    {
        ActivityKind.Uploaded => "Uploaded",
        ActivityKind.Downloaded => "Downloaded",
        ActivityKind.DeletedHere => "Deleted here",
        ActivityKind.DeletedOnServer => "Deleted on server",
        ActivityKind.Conflict => "Conflict copy kept",
        ActivityKind.Warning => "Needs attention",
        ActivityKind.Blocked => "Waiting for you",
        ActivityKind.Offline => "Offline",
        ActivityKind.Error => "Problem",
        _ => "Info",
    };

    /// <summary>Main line of a list row: the file name, or the message when there is no file.</summary>
    public string Primary => Path is null ? Text : Paths.PathRules.FileName(Path);

    /// <summary>Second line: kind and folder ("Uploaded · Projects/2026").</summary>
    public string Secondary
    {
        get
        {
            if (Path is null)
                return Title;
            var folder = Paths.PathRules.Parent(Path);
            return folder is null ? Title : $"{Title} · {folder}";
        }
    }

    /// <summary>"2 min ago", computed when read (lists are redrawn regularly).</summary>
    public string WhenText => Format.RelativeTime(Time, DateTimeOffset.Now);

    /// <summary>The colour of the row's glyph: "move" (uploads and downloads), "warn", "bad" or "muted".</summary>
    public string Tone => ToneOf(Kind);

    internal static string ToneOf(ActivityKind kind) => kind switch
    {
        ActivityKind.Uploaded or ActivityKind.Downloaded => "move",
        ActivityKind.Conflict or ActivityKind.Blocked => "warn",
        ActivityKind.DeletedHere or ActivityKind.DeletedOnServer or ActivityKind.Warning or ActivityKind.Error => "bad",
        _ => "muted",
    };
}

/// <summary>Bounded, thread-safe list of recent activity, newest first.</summary>
public sealed class ActivityFeed(int capacity = 200)
{
    private readonly LinkedList<ActivityItem> _items = new();
    private readonly object _gate = new();

    public event Action<ActivityItem>? Added;

    public int Capacity { get; } = capacity;

    public void Add(ActivityItem item)
    {
        lock (_gate)
        {
            _items.AddFirst(item);
            while (_items.Count > Capacity)
                _items.RemoveLast();
        }
        Added?.Invoke(item);
    }

    public void Add(ActivityKind kind, string? path, string text, TimeProvider? clock = null) =>
        Add(new ActivityItem((clock ?? TimeProvider.System).GetUtcNow(), kind, path, text));

    /// <summary>Snapshot, newest first.</summary>
    public IReadOnlyList<ActivityItem> Items
    {
        get
        {
            lock (_gate)
                return _items.ToList();
        }
    }
}

/// <summary>A day heading in the full activity list ("Today", "Yesterday", "Monday 5 October").</summary>
public sealed record ActivityDay(string Text, DateTime Day);

/// <summary>The Activity page's rows: newest first, with a heading before each day.</summary>
public static class ActivityDays
{
    public static IReadOnlyList<object> Rows(IReadOnlyList<ActivityItem> items, DateTimeOffset now)
    {
        var rows = new List<object>(items.Count + 4);
        DateTime? day = null;
        foreach (var item in items)
        {
            var d = item.Time.ToLocalTime().Date;
            if (d != day)
            {
                rows.Add(new ActivityDay(Format.Day(d, now), d));
                day = d;
            }
            rows.Add(item);
        }
        return rows;
    }

    /// <summary>
    /// Like <see cref="Rows"/>, with runs of same-folder files folded into <see cref="ActivityFolder"/> rows
    /// (a run never crosses a day heading).
    /// </summary>
    public static IReadOnlyList<object> GroupedRows(IReadOnlyList<ActivityItem> items, DateTimeOffset now)
    {
        var rows = new List<object>(items.Count + 4);
        foreach (var day in items.GroupBy(i => i.Time.ToLocalTime().Date))
        {
            rows.Add(new ActivityDay(Format.Day(day.Key, now), day.Key));
            rows.AddRange(ActivityGroups.Group(day.ToList()));
        }
        return rows;
    }
}
