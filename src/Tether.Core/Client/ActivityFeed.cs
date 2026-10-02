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
