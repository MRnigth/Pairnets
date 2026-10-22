using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The back and forward buttons above every page (<see cref="PageHistory{TPage}"/>, the same as the Windows app), with
/// Alt+Left and Alt+Right (⌘[ and ⌘] on a Mac), a keyboard's Back and Forward keys and the mouse's side buttons. Files and
/// its Deleted tab are one page.
/// </summary>
public partial class MainWindow
{
    private readonly PageHistory<MainPage> _history = new((a, b) => a == b || (IsFiles(a) && IsFiles(b)));

    /// <summary>True while back or forward opens a page, so the trip is not noted as a new one.</summary>
    private bool _stepping;

    private static bool IsFiles(MainPage page) => page is MainPage.Files or MainPage.History;

    internal bool CanGoBack => _history.CanGoBack;

    internal bool CanGoForward => _history.CanGoForward;

    private void WireHistory() =>
        AddHandler(PointerPressedEvent, OnSideButton, RoutingStrategies.Tunnel); // before a list or button keeps the press

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
        var mac = OperatingSystem.IsMacOS();
        BackButton.IsEnabled = _history.CanGoBack;
        ForwardButton.IsEnabled = _history.CanGoForward;
        ToolTip.SetTip(BackButton, Shortcuts.WithHint(_history.BackTo is { } back ? "Back to " + PageName(back) : "Back", ShortcutAction.Back, mac));
        ToolTip.SetTip(ForwardButton, Shortcuts.WithHint(_history.ForwardTo is { } forward ? "Forward to " + PageName(forward) : "Forward", ShortcutAction.Forward, mac));
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

    private void OnBack(object? sender, RoutedEventArgs e) => GoBack();

    private void OnForward(object? sender, RoutedEventArgs e) => GoForward();

    /// <summary>The mouse's side buttons.</summary>
    private void OnSideButton(object? sender, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed))
            return;
        Run(kind == PointerUpdateKind.XButton1Pressed ? ShortcutAction.Back : ShortcutAction.Forward);
        e.Handled = true;
    }
}
