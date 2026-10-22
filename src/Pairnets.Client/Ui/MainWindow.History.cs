using System.Windows;
using System.Windows.Input;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The back and forward buttons above every page (<see cref="PageHistory{TPage}"/>, the same as the Mac/Linux app), with
/// Alt+Left and Alt+Right, a keyboard's Back and Forward keys and the mouse's side buttons. Files and its Deleted tab are one page.
/// </summary>
public partial class MainWindow
{
    private readonly PageHistory<MainPage> _history = new((a, b) => a == b || (IsFiles(a) && IsFiles(b)));

    /// <summary>True while back or forward opens a page, so the trip is not noted as a new one.</summary>
    private bool _stepping;

    private static bool IsFiles(MainPage page) => page is MainPage.Files or MainPage.History;

    internal bool CanGoBack => _history.CanGoBack;

    internal bool CanGoForward => _history.CanGoForward;

    /// <summary>Notes a page change (from <see cref="ShowPage"/>) and redraws the two buttons.</summary>
    private void NoteVisit(MainPage from, MainPage to)
    {
        if (!_stepping)
            _history.Visit(from, to);
        DrawPageNav();
    }

    internal void GoBack() => Step(_history.Back(Page));

    internal void GoForward() => Step(_history.Forward(Page));

    private void Step(MainPage? page)
    {
        if (page is not { } to)
            return;
        _stepping = true;
        try
        {
            if (to == MainPage.Settings && _actions is not null)
                _actions.ShowSettings(); // a fresh form, as from its tile
            else
                Navigate(to);
        }
        finally
        {
            _stepping = false;
        }
    }

    private void DrawPageNav()
    {
        BackButton.IsEnabled = _history.CanGoBack;
        ForwardButton.IsEnabled = _history.CanGoForward;
        BackButton.ToolTip = Shortcuts.WithHint(_history.BackTo is { } back ? "Back to " + PageName(back) : "Back", ShortcutAction.Back, mac: false);
        ForwardButton.ToolTip = Shortcuts.WithHint(_history.ForwardTo is { } forward ? "Forward to " + PageName(forward) : "Forward", ShortcutAction.Forward, mac: false);
    }

    /// <summary>The page's name as its tile says it.</summary>
    internal static string PageName(MainPage page) => page switch
    {
        MainPage.Activity => "Activity",
        MainPage.Files => "Files",
        MainPage.History => "Deleted files",
        MainPage.Devices => "Computers",
        MainPage.Attention => "Needs you",
        MainPage.Settings => "Settings",
        MainPage.Account => "Account",
        _ => "Overview",
    };

    private void OnBack(object sender, RoutedEventArgs e) => GoBack();

    private void OnForward(object sender, RoutedEventArgs e) => GoForward();

    /// <summary>The mouse's side buttons.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.Handled || e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return;
        Run(e.ChangedButton == MouseButton.XButton1 ? ShortcutAction.Back : ShortcutAction.Forward);
        e.Handled = true;
    }
}
