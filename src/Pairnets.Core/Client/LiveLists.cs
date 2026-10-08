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
/// A row of "In progress" (a file, or a folder of this sync) as the screens show it. Updated in place, so its bar
/// glides and a folder someone opened stays open while its files move.
/// </summary>
public sealed class TransferRowView(string key, bool isFolder) : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _detail = string.Empty;
    private double _percent;
    private string _percentText = string.Empty;
    private bool _isUpload = true;
    private IReadOnlyList<BatchFile> _files = [];

    public string Key { get; } = key;

    public bool IsFolder { get; } = isFolder;

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    /// <summary>"2 of 18 files" for a folder, the folder for a file.</summary>
    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public double PercentValue
    {
        get => _percent;
        private set => Set(ref _percent, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => Set(ref _percentText, value);
    }

    public bool IsUpload
    {
        get => _isUpload;
        private set => Set(ref _isUpload, value);
    }

    /// <summary>Every file of this sync in the folder (done, moving, waiting).</summary>
    public IReadOnlyList<BatchFile> Files
    {
        get => _files;
        private set
        {
            if (_files.SequenceEqual(value))
                return;
            _files = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Files)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(TransferGroup g)
    {
        Title = g.Title;
        Detail = g.Detail;
        PercentValue = g.PercentValue;
        PercentText = g.PercentText;
        IsUpload = g.IsUpload;
        Files = g.Files;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Keeps the screens' lists in step with the session without rebuilding them.</summary>
public static class LiveLists
{
    /// <summary>"In progress" rows: kept by key and updated in place, in the order <paramref name="now"/> gives.</summary>
    public static void Sync(ObservableCollection<TransferRowView> shown, IReadOnlyList<TransferGroup> now)
    {
        var wanted = now.Select(g => (g.IsFolder ? "d:" : "f:") + g.Key).ToList();
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains((shown[i].IsFolder ? "d:" : "f:") + shown[i].Key))
                shown.RemoveAt(i);
        }
        for (var i = 0; i < now.Count; i++)
        {
            var g = now[i];
            var at = -1;
            for (var j = 0; j < shown.Count; j++)
            {
                if (shown[j].Key == g.Key && shown[j].IsFolder == g.IsFolder)
                {
                    at = j;
                    break;
                }
            }
            TransferRowView view;
            if (at < 0)
            {
                view = new TransferRowView(g.Key, g.IsFolder);
                shown.Insert(Math.Min(i, shown.Count), view);
            }
            else
            {
                view = shown[at];
                if (at != i && i < shown.Count)
                    shown.Move(at, i);
            }
            view.Update(g);
        }
    }

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
