using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pairnets.Core.Client;

/// <summary>
/// A file in progress as the screens show it. It is updated in place (not replaced) so its
/// progress bar can glide to the new value and its row keeps its entrance animation.
/// </summary>
public sealed class ActiveFileView(string path, string operation) : INotifyPropertyChanged
{
    private double _percent;
    private bool _hasPercent;

    public string Path { get; } = path;

    public string FileName { get; } = Paths.PathRules.FileName(path);

    public bool IsUpload { get; } = operation == "upload";

    public double PercentValue
    {
        get => _percent;
        private set => Set(ref _percent, value);
    }

    public bool HasPercent
    {
        get => _hasPercent;
        private set => Set(ref _hasPercent, value);
    }

    public string PercentText => HasPercent ? ((int)PercentValue).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ActiveTransfer t)
    {
        var before = PercentText;
        HasPercent = t.Percent is not null;
        PercentValue = t.Percent ?? 0;
        if (PercentText != before)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PercentText)));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// A computer on the Devices page as the screens show it. Its live numbers change every second while it transfers, so
/// it is updated in place (the row is not rebuilt and does not play its entrance again).
/// </summary>
public sealed class DeviceRowView(DeviceRow row) : INotifyPropertyChanged
{
    private DeviceRow _row = row;

    public string Name => _row.Name;

    public string Title => _row.Title;

    public bool Online => _row.Online;

    public string StatusText => _row.StatusText;

    public string Detail => _row.Detail;

    public bool IsThisComputer => _row.IsThisComputer;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(DeviceRow row)
    {
        var before = _row;
        _row = row;
        foreach (var (name, changed) in new[]
        {
            (nameof(Title), before.Title != row.Title),
            (nameof(Online), before.Online != row.Online),
            (nameof(StatusText), before.StatusText != row.StatusText),
            (nameof(Detail), before.Detail != row.Detail),
        })
        {
            if (changed)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>Keeps the screens' lists in step with the session without rebuilding them.</summary>
public static class LiveLists
{
    /// <summary>Adds new transfers at the end, updates running ones in place, removes finished ones.</summary>
    public static void Sync(ObservableCollection<ActiveFileView> shown, IReadOnlyList<ActiveTransfer> now)
    {
        var wanted = now.ToDictionary(t => t.Path, StringComparer.Ordinal);
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!wanted.ContainsKey(shown[i].Path))
                shown.RemoveAt(i);
        }
        var have = shown.ToDictionary(v => v.Path, StringComparer.Ordinal);
        foreach (var t in now)
        {
            if (!have.TryGetValue(t.Path, out var view))
            {
                view = new ActiveFileView(t.Path, t.Operation);
                shown.Add(view);
            }
            view.Update(t);
        }
    }

    /// <summary>
    /// The Devices page: rows stay in place and are updated (live speeds change every second); a different set or
    /// order of computers rebuilds the list.
    /// </summary>
    public static void Sync(ObservableCollection<DeviceRowView> shown, IReadOnlyList<DeviceRow> now)
    {
        var same = shown.Count == now.Count;
        for (var i = 0; same && i < now.Count; i++)
            same = string.Equals(shown[i].Name, now[i].Name, StringComparison.Ordinal) && shown[i].IsThisComputer == now[i].IsThisComputer;
        if (!same)
        {
            shown.Clear();
            foreach (var row in now)
                shown.Add(new DeviceRowView(row));
            return;
        }
        for (var i = 0; i < now.Count; i++)
            shown[i].Update(now[i]);
    }

    /// <summary>
    /// Activity is newest first: new items are inserted at the top (so only they animate in),
    /// the oldest ones fall off the end. Anything unexpected rebuilds the list.
    /// </summary>
    public static void Sync(ObservableCollection<ActivityItem> shown, IReadOnlyList<ActivityItem> now)
    {
        var newCount = shown.Count == 0 ? now.Count : IndexOf(now, shown[0]);
        if (newCount < 0)
        {
            shown.Clear();
            foreach (var item in now)
                shown.Add(item);
            return;
        }
        for (var i = newCount - 1; i >= 0; i--)
            shown.Insert(0, now[i]);
        while (shown.Count > now.Count)
            shown.RemoveAt(shown.Count - 1);
    }

    /// <summary>
    /// Brings a list of rows (activity items by identity, headings by value) to <paramref name="wanted"/>
    /// with the fewest changes, so existing rows stay put and only new ones animate in.
    /// </summary>
    public static void Sync(ObservableCollection<object> shown, IReadOnlyList<object> wanted)
    {
        var keep = new HashSet<object>(wanted, RowComparer.Instance);
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(shown[i]))
                shown.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < shown.Count && RowComparer.Instance.Equals(shown[i], wanted[i]))
                continue;
            var at = -1;
            for (var j = i + 1; j < shown.Count; j++)
            {
                if (RowComparer.Instance.Equals(shown[j], wanted[i]))
                {
                    at = j;
                    break;
                }
            }
            if (at >= 0)
                shown.Move(at, i);
            else
                shown.Insert(i, wanted[i]);
        }
        while (shown.Count > wanted.Count)
            shown.RemoveAt(shown.Count - 1);
    }

    /// <summary>Activity items are the same row only if they are the same object; anything else compares by value.</summary>
    private sealed class RowComparer : IEqualityComparer<object>
    {
        public static readonly RowComparer Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is ActivityItem || y is ActivityItem ? ReferenceEquals(x, y) : object.Equals(x, y);

        public int GetHashCode(object obj) =>
            obj is ActivityItem ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj) : obj.GetHashCode();
    }

    private static int IndexOf(IReadOnlyList<ActivityItem> list, ActivityItem item)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
                return i;
        }
        return -1;
    }
}
