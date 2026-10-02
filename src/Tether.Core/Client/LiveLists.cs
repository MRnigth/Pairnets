using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Tether.Core.Client;

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
