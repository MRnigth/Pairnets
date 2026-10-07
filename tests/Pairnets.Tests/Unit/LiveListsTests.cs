using System.Collections.ObjectModel;
using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

public class LiveListsTests
{
    [Fact]
    public void ActiveFilesAreUpdatedInPlaceAddedAndRemoved()
    {
        var shown = new ObservableCollection<ActiveFileView>();
        LiveLists.Sync(shown, [new ActiveTransfer("a", "upload", 10, 100), new ActiveTransfer("b", "download", 0, 0)]);
        var a = shown[0];
        Assert.Equal(2, shown.Count);
        Assert.False(shown[1].HasPercent);

        var changed = new List<string?>();
        a.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        LiveLists.Sync(shown, [new ActiveTransfer("a", "upload", 60, 100), new ActiveTransfer("c", "upload", 1, 2)]);
        Assert.Same(a, shown[0]); // same object: its bar glides instead of being rebuilt
        Assert.Equal(60, a.PercentValue);
        Assert.Equal("60%", a.PercentText);
        Assert.Contains(nameof(ActiveFileView.PercentValue), changed);
        Assert.Equal(["a", "c"], shown.Select(v => v.Path));
        Assert.True(shown[0].IsUpload);
    }

    [Fact]
    public void ActivityInsertsOnlyNewItemsAtTheTop()
    {
        var t = DateTimeOffset.UnixEpoch;
        var one = new ActivityItem(t, ActivityKind.Uploaded, "1", "1");
        var two = new ActivityItem(t, ActivityKind.Uploaded, "2", "2");
        var three = new ActivityItem(t, ActivityKind.Uploaded, "3", "3");
        var shown = new ObservableCollection<ActivityItem>();
        LiveLists.Sync(shown, [two, one]);
        var inserted = 0;
        shown.CollectionChanged += (_, e) => inserted += e.NewItems?.Count ?? 0;
        LiveLists.Sync(shown, [three, two, one]);
        Assert.Equal(1, inserted);
        Assert.Equal([three, two, one], shown);
        LiveLists.Sync(shown, [three, two]); // the oldest fell off
        Assert.Equal([three, two], shown);
        LiveLists.Sync(shown, [one]); // unknown order: rebuilt
        Assert.Equal([one], shown);
    }
}
