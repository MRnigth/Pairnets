using Pairnets.Core.Client;

namespace Pairnets.Tests.Unit;

/// <summary>The main window's back and forward buttons (<see cref="PageHistory{TPage}"/>), shared by both apps.</summary>
public class PageHistoryTests
{
    private enum Page
    {
        Overview,
        Activity,
        Files,
        Deleted,
        Settings,
    }

    private static PageHistory<Page> Tabs() =>
        new((a, b) => a == b || (a is Page.Files or Page.Deleted && b is Page.Files or Page.Deleted));

    [Fact]
    public void NothingToGoBackToAtFirst()
    {
        var history = new PageHistory<Page>();
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back(Page.Overview));
        Assert.Null(history.Forward(Page.Overview));
        Assert.False(history.CanGoForward); // a back with nowhere to go leaves nothing behind
    }

    [Fact]
    public void BackAndForwardRetraceThePath()
    {
        var history = new PageHistory<Page>();
        history.Visit(Page.Overview, Page.Activity);
        history.Visit(Page.Activity, Page.Files);
        Assert.Equal(Page.Activity, history.BackTo);
        Assert.Equal(Page.Activity, history.Back(Page.Files));
        Assert.Equal(Page.Overview, history.Back(Page.Activity));
        Assert.False(history.CanGoBack);
        Assert.Equal(Page.Activity, history.ForwardTo);
        Assert.Equal(Page.Activity, history.Forward(Page.Overview));
        Assert.Equal(Page.Files, history.Forward(Page.Activity));
        Assert.False(history.CanGoForward);
        Assert.Equal(Page.Activity, history.BackTo);
    }

    [Fact]
    public void OpeningAPageAfterGoingBackClearsForward()
    {
        var history = new PageHistory<Page>();
        history.Visit(Page.Overview, Page.Activity);
        history.Back(Page.Activity);
        Assert.True(history.CanGoForward);
        history.Visit(Page.Overview, Page.Settings);
        Assert.False(history.CanGoForward);
        Assert.Equal(Page.Overview, history.BackTo);
    }

    [Fact]
    public void TheSamePageIsNoStep()
    {
        var history = new PageHistory<Page>();
        history.Visit(Page.Overview, Page.Overview);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void TabsOfOnePageAreNoStep()
    {
        var history = Tabs();
        history.Visit(Page.Activity, Page.Files);
        history.Visit(Page.Files, Page.Deleted);
        Assert.Equal(Page.Activity, history.Back(Page.Deleted));
        Assert.Equal(Page.Deleted, history.Forward(Page.Activity)); // forward returns to the tab that was open
    }

    [Fact]
    public void OnlyTheLastStepsAreKept()
    {
        var history = new PageHistory<Page>();
        for (var i = 0; i < PageHistory<Page>.Most + 10; i++)
            history.Visit(i % 2 == 0 ? Page.Overview : Page.Activity, i % 2 == 0 ? Page.Activity : Page.Overview);
        var steps = 0;
        var current = Page.Overview;
        while (history.Back(current) is { } page)
        {
            current = page;
            steps++;
        }
        Assert.Equal(PageHistory<Page>.Most, steps);
    }
}
