using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The main window's keyboard shortcuts (<see cref="Shortcuts"/>, the same as the Windows app) and, on a Mac, its menu bar.
/// Ctrl+F search, Ctrl+R and F5 sync now, Ctrl+, Settings, Ctrl+1…6 the pages; on a Mac the Command key (⌘) instead of Ctrl.
/// </summary>
public partial class MainWindow
{
    private NativeMenuItem? _pauseMenuItem;
    private NativeMenuItem? _foldMenuItem;

    /// <summary>The key that stands for Ctrl in the shortcuts: Command on a Mac.</summary>
    private static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void WireShortcuts()
    {
        // Tunnel, so a focused text box does not keep Ctrl+R or Ctrl+1 to itself.
        AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
        HintShortcuts();
        if (OperatingSystem.IsMacOS())
        {
            var (app, window) = MakeMacMenus();
            if (Application.Current is { } application)
                NativeMenu.SetMenu(application, app);
            NativeMenu.SetMenu(this, window);
            SideFoldedChanged += _ => RefreshMacMenu();
        }
    }

    /// <summary>Adds the shortcut to the tooltips that have one (the dock's page buttons).</summary>
    private void HintShortcuts()
    {
        var mac = OperatingSystem.IsMacOS();
        foreach (var (dock, action) in new (RadioButton, ShortcutAction)[]
                 {
                     (DockOverview, ShortcutAction.PageOverview), (DockActivity, ShortcutAction.PageActivity), (DockAttention, ShortcutAction.PageNeedsYou),
                     (DockHistory, ShortcutAction.PageHistory), (DockDevices, ShortcutAction.PageDevices), (DockSettings, ShortcutAction.PageSettings),
                 })
        {
            if (ToolTip.GetTip(dock) is string tip)
                ToolTip.SetTip(dock, Shortcuts.WithHint(tip, action, mac));
        }
    }

    private void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        var command = CommandKey;
        if (e.KeyModifiers != KeyModifiers.None && e.KeyModifiers != command)
            return;
        if (Run(Shortcuts.For(e.Key.ToString(), e.KeyModifiers == command)))
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
                    MainPage.Activity => ActivitySearch,
                    MainPage.History => HistorySearch,
                    _ => null,
                };
                if (box is not { IsEffectivelyVisible: true })
                    return false;
                box.Focus();
                box.SelectAll();
                return true;
            case ShortcutAction.SyncNow:
                if (_overview?.SyncNow.IsEnabled != false)
                    _actions?.SyncNow(); // not while a sync is already running
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
            case ShortcutAction.PageHistory:
                GoTo(MainPage.History);
                return true;
            case ShortcutAction.PageDevices:
                GoTo(MainPage.Devices);
                return true;
            default:
                return false;
        }
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

    // ------------------------------------------------------------------ the Mac menu bar

    /// <summary>
    /// The Mac menu bar: the app menu (About, Settings… ⌘,) and File, Sync, View, Window and Help. Built only on a Mac;
    /// the checks build it anywhere to look at it.
    /// </summary>
    internal (NativeMenu App, NativeMenu Window) MakeMacMenus()
    {
        var cmd = KeyModifiers.Meta;
        var app = new NativeMenu();
        app.Items.Add(Item("About Pairnets", ShowAbout));
        app.Items.Add(new NativeMenuItemSeparator());
        app.Items.Add(Item("Settings…", () => Run(ShortcutAction.Settings), new KeyGesture(Key.OemComma, cmd)));

        var file = Menu("File", Item("Open Folder", () => _actions?.OpenFolder(), new KeyGesture(Key.O, cmd)));

        _pauseMenuItem = Item(_status.Paused ? "Resume Syncing" : "Pause Syncing", () => _actions?.TogglePause());
        var sync = Menu("Sync", Item("Sync Now", () => Run(ShortcutAction.SyncNow), new KeyGesture(Key.R, cmd)), _pauseMenuItem);

        _foldMenuItem = Item(SideFolded ? "Open Side Panel" : "Fold Side Panel", () => SetFolded(!SideFolded), new KeyGesture(Key.S, cmd | KeyModifiers.Control));
        var view = Menu("View",
            Item("Overview", () => Run(ShortcutAction.PageOverview), new KeyGesture(Key.D1, cmd)),
            Item("Activity", () => Run(ShortcutAction.PageActivity), new KeyGesture(Key.D2, cmd)),
            Item("Needs You", () => Run(ShortcutAction.PageNeedsYou), new KeyGesture(Key.D3, cmd)),
            Item("History", () => Run(ShortcutAction.PageHistory), new KeyGesture(Key.D4, cmd)),
            Item(NavLabel(NavDevices, "Devices"), () => Run(ShortcutAction.PageDevices), new KeyGesture(Key.D5, cmd)),
            Item("Settings", () => Run(ShortcutAction.PageSettings), new KeyGesture(Key.D6, cmd)),
            new NativeMenuItemSeparator(),
            _foldMenuItem);

        var window = Menu("Window",
            Item("Minimize", () => WindowState = WindowState.Minimized, new KeyGesture(Key.M, cmd)),
            Item("Zoom", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
            new NativeMenuItemSeparator(),
            Item("Close Window", Close, new KeyGesture(Key.W, cmd)));

        var help = Menu("Help", Item("Report a Bug…", () => _actions?.ReportBug()));

        var bar = new NativeMenu();
        foreach (var top in new[] { file, sync, view, window, help })
            bar.Items.Add(top);
        return (app, bar);
    }

    /// <summary>Keeps the menu's words true: Pause or Resume, Fold or Open the side panel.</summary>
    private void RefreshMacMenu()
    {
        if (_pauseMenuItem is not null)
            _pauseMenuItem.Header = _status.Paused ? "Resume Syncing" : "Pause Syncing";
        if (_foldMenuItem is not null)
            _foldMenuItem.Header = SideFolded ? "Open Side Panel" : "Fold Side Panel";
    }

    /// <summary>The words of a page tile (so the menu follows the tile's name).</summary>
    private static string NavLabel(RadioButton tile, string fallback) =>
        tile.Content is Grid grid && grid.Children.OfType<TextBlock>().FirstOrDefault()?.Text is { Length: > 0 } text ? text : fallback;

    private static NativeMenuItem Item(string header, Action act, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header);
        if (gesture is not null)
            item.Gesture = gesture;
        item.Click += (_, _) => act();
        return item;
    }

    private static NativeMenuItem Menu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
            menu.Items.Add(item);
        return new NativeMenuItem(header) { Menu = menu };
    }

    private async void ShowAbout() =>
        await Dialogs.InfoAsync(this, "About Pairnets", $"Pairnets {PairnetsInfo.ProductVersion}\n\n{PairnetsLinks.AboutLine}");
}
