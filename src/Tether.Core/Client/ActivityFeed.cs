namespace Tether.Core.Client;

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
