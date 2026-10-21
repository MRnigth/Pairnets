using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The main window's keyboard shortcuts (<see cref="Shortcuts"/>, the same as the Mac/Linux app): Ctrl+F search, Ctrl+R and
/// F5 sync now, Ctrl+, Settings, Ctrl+1…6 the pages. The tooltips name them.
/// </summary>
public partial class MainWindow
{
    /// <summary>Adds the shortcut to the tooltips that have one (the dock's page buttons, Sync now).</summary>
    private void HintShortcuts()
    {
        foreach (var (dock, action) in new (RadioButton, ShortcutAction)[]
                 {
                     (DockOverview, ShortcutAction.PageOverview), (DockActivity, ShortcutAction.PageActivity), (DockAttention, ShortcutAction.PageNeedsYou),
                     (DockFiles, ShortcutAction.PageFiles), (DockDevices, ShortcutAction.PageDevices), (DockSettings, ShortcutAction.PageSettings),
                 })
        {
            if (dock.ToolTip is string tip)
                dock.ToolTip = Shortcuts.WithHint(tip, action, mac: false);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
            return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers is not (ModifierKeys.None or ModifierKeys.Control))
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Run(Shortcuts.For(key.ToString(), modifiers == ModifierKeys.Control)))
            e.Handled = true;
    }

    /// <summary>Does what a shortcut stands for; false when it does nothing here (Ctrl+F on a page without a search box).</summary>
    internal bool Run(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.Search:
                var box = Page switch
                {
                    MainPage.Activity => ActivityPage.Search,
                    MainPage.Files => NestFilesPage.FilesSearch,
                    MainPage.History => HistoryPage.HistorySearch,
                    _ => null,
                };
                if (box is null || !Shown(box))
                    return false;
                box.FocusInput();
                return true;
            case ShortcutAction.SyncNow:
                if (_overview?.SyncNow.IsEnabled == false)
                    return true; // a sync is already running
                _actions?.SyncNow();
                return true;
            case ShortcutAction.Settings:
            case ShortcutAction.PageSettings:
                GoTo(MainPage.Settings);
                return true;
            case ShortcutAction.PageOverview:
                GoTo(MainPage.Overview);
                return true;
            case ShortcutAction.PageActivity:
                GoTo(MainPage.Activity);
                return true;
            case ShortcutAction.PageNeedsYou:
                GoTo(MainPage.Attention);
                return true;
            case ShortcutAction.PageFiles:
                GoTo(MainPage.Files);
                return true;
            case ShortcutAction.PageDevices:
                GoTo(MainPage.Devices);
                return true;
            default:
                return false;
        }
    }

    /// <summary>True when nothing between <paramref name="element"/> and the window is collapsed (right after a page change, before layout).</summary>
    private static bool Shown(DependencyObject element)
    {
        for (var e = element; e is not null; e = System.Windows.Media.VisualTreeHelper.GetParent(e) ?? LogicalTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: not Visibility.Visible })
                return false;
        }
        return true;
    }

    /// <summary>Opens a page as its tile would (Settings asks the app for a fresh form).</summary>
    private void GoTo(MainPage page)
    {
        if (page == MainPage.Settings && _actions is not null)
        {
            if (Page != MainPage.Settings)
                _actions.ShowSettings();
            return;
        }
        Navigate(page);
    }
}
