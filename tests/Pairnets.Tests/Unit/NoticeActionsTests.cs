using Pairnets.Desktop.Platform;

namespace Pairnets.Tests.Unit;

public class NoticeActionsTests
{
    [Fact]
    public void AClickedNotice_FindsItsAction()
    {
        var actions = new NoticeActions();
        var ran = 0;
        actions.Add("a", () => ran++);
        actions.Get("a")!();
        actions.Get("a")!(); // a notice can be clicked again in Notification Center
        Assert.Equal(2, ran);
    }

    [Fact]
    public void AnUnknownNoticeOrOneWithoutAction_DoesNothing()
    {
        var actions = new NoticeActions();
        actions.Add("none", null);
        Assert.Null(actions.Get("none"));
        Assert.Null(actions.Get("other"));
    }

    [Fact]
    public void OnlyTheNewestNoticesAreKept()
    {
        var actions = new NoticeActions(capacity: 3);
        for (var i = 0; i < 5; i++)
            actions.Add("n" + i, () => { });
        Assert.Null(actions.Get("n0"));
        Assert.Null(actions.Get("n1"));
        Assert.NotNull(actions.Get("n2"));
        Assert.NotNull(actions.Get("n4"));
    }
}
