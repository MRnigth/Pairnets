namespace Pairnets.Core.Client;

/// <summary>
/// The main window's back and forward buttons, the same in both apps: the pages you came from, and the ones you went back
/// from. Opening a page another way (a tile, a link, a shortcut) starts a new path, so forward is cleared, as in a browser.
/// </summary>
/// <typeparam name="TPage">The app's page enum.</typeparam>
public sealed class PageHistory<TPage>
    where TPage : struct, Enum
{
    /// <summary>How many steps back are kept; older ones fall off.</summary>
    public const int Most = 30;

    private readonly List<TPage> _back = [];
    private readonly List<TPage> _forward = [];
    private readonly Func<TPage, TPage, bool> _samePage;

    /// <param name="samePage">
    /// True for two pages that count as one, so moving between them is no step (the Files page's two tabs). By default only
    /// a page and itself.
    /// </param>
    public PageHistory(Func<TPage, TPage, bool>? samePage = null) =>
        _samePage = samePage ?? ((a, b) => EqualityComparer<TPage>.Default.Equals(a, b));

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>The page back would open; null when there is none.</summary>
    public TPage? BackTo => CanGoBack ? _back[^1] : null;

    /// <summary>The page forward would open; null when there is none.</summary>
    public TPage? ForwardTo => CanGoForward ? _forward[^1] : null;

    /// <summary>Notes a move from <paramref name="from"/> to <paramref name="to"/> that was not back or forward.</summary>
    public void Visit(TPage from, TPage to)
    {
        if (_samePage(from, to))
            return;
        Push(_back, from);
        _forward.Clear();
    }

    /// <summary>Steps back from <paramref name="current"/>: the page to open, or null when there is nothing to go back to.</summary>
    public TPage? Back(TPage current) => Step(_back, _forward, current);

    /// <summary>Steps forward from <paramref name="current"/>: the page to open, or null when there is none.</summary>
    public TPage? Forward(TPage current) => Step(_forward, _back, current);

    private static TPage? Step(List<TPage> from, List<TPage> to, TPage current)
    {
        if (from.Count == 0)
            return null;
        var page = from[^1];
        from.RemoveAt(from.Count - 1);
        Push(to, current);
        return page;
    }

    private static void Push(List<TPage> list, TPage page)
    {
        list.Add(page);
        if (list.Count > Most)
            list.RemoveAt(0);
    }
}
