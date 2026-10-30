using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// The Developer beta look's top bar (docs/DECISIONS.md, "Developer beta look"): logo, the five page pills, the update
/// pill, the bell (the old "Needs you" tile) and the account. It replaces the left side panel, which stays in the window
/// (hidden) so the stable look and its tests are untouched. Everything here exists only when <see cref="DevBetaLook.On"/>.
/// The pills reuse the side tiles' own radio buttons (so <c>OnNav</c> and <c>Navigate</c> do the work), the update pill the
/// update card's handlers, the account button <c>OnAccount</c>, the nest pill <c>IMainActions.SelectNest</c>.
/// </summary>
public partial class MainWindow
{
    private Border? _topBar;
    private readonly Dictionary<MainPage, Button> _topPills = [];
    private Button? _bell;
    private Border? _bellBadge;
    private TextBlock? _bellBadgeText;
    private Button? _updatePill;
    private TextBlock? _updatePillText;
    private Button? _topAccount;
    private TextBlock? _topAccountName;
    private TextBlock? _topAccountInitial;
    private Button? _nestPill;
    private TextBlock? _nestPillText;
    private SidePanelModel? _sideModel;

    /// <summary>True when this window shows the top bar instead of the side panel (for the tests).</summary>
    internal bool HasTopBar => _topBar is not null;

    private void BuildTopBar()
    {
        // The side panel stays, hidden; its column goes.
        Root.ColumnDefinitions[0].Width = new GridLength(0);
        SideOpen.IsVisible = false;
        SideDock.IsVisible = false;
        Root.RowDefinitions = new RowDefinitions("Auto,*,Auto");
        if (Root.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetColumn(g) == 1 && g != SideOpen && g != SideDock) is { } page)
            Grid.SetRow(page, 1);

        Styles.Add(new Style(x => x.OfType<TextBlock>().Class("title"))
        {
            Setters =
            {
                new Setter(TextBlock.FontFamilyProperty, Visuals.DisplayFamily()),
                new Setter(TextBlock.FontWeightProperty, FontWeight.Bold),
                new Setter(TextBlock.FontSizeProperty, 34.0),
            },
        });

        // ---- left: logo
        var logo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 9, Margin = new Thickness(4, 0, 6, 0) };
        logo.Children.Add(new Image { Source = AppIcon.Source, Width = 30, Height = 30 });
        var word = new TextBlock { Text = "Pairnets", FontSize = 17, FontWeight = FontWeight.ExtraBold, VerticalAlignment = VerticalAlignment.Center };
        logo.Children.Add(word);

        // ---- middle: the pills (they wrap in a narrow window)
        var pills = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        foreach (var (page2, label) in new[] { (MainPage.Overview, "Overview"), (MainPage.Activity, "Activity"), (MainPage.Files, "Files"), (MainPage.Devices, "Computers"), (MainPage.Settings, "Settings") })
        {
            var pill = MakePill(label);
            var target = page2;
            pill.Click += (_, _) => NavFor(target).IsChecked = true; // the side tile's radio button: OnNav does the rest
            _topPills[page2] = pill;
            pills.Children.Add(pill);
        }

        // ---- right: update pill, bell, account
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };

        _updatePillText = new TextBlock { FontSize = 13, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        Paint(_updatePillText, TextBlock.ForegroundProperty, "T.AccentInk");
        var updateIcon = new LineIcon { Width = 15, Height = 15, StrokeThickness = 2.2, Data = Visuals.Resource<Geometry>("I.Download") };
        Paint(updateIcon, LineIcon.StrokeProperty, "T.AccentInk");
        _updatePill = new Button
        {
            Height = 34, CornerRadius = new CornerRadius(17), Padding = new Thickness(13, 0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { updateIcon, _updatePillText } },
        };
        Paint(_updatePill, BackgroundProperty, "T.AccentSoft");
        _updatePill.BorderThickness = new Thickness(0);
        var updateFlyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        updateFlyout.Opening += (_, _) => updateFlyout.Content = BuildUpdateFlyout(updateFlyout);
        _updatePill.Flyout = updateFlyout;
        right.Children.Add(_updatePill);

        var bellIcon = new LineIcon { Width = 18, Height = 18, StrokeThickness = 2, Data = Visuals.Resource<Geometry>("I.Bell") };
        Paint(bellIcon, LineIcon.StrokeProperty, "T.Text");
        _bellBadgeText = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Paint(_bellBadgeText, TextBlock.ForegroundProperty, "T.NeedsActiveText");
        _bellBadge = new Border
        {
            MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), Padding = new Thickness(3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -3, 0),
            IsHitTestVisible = false, IsVisible = false, Child = _bellBadgeText,
        };
        Paint(_bellBadge, Border.BackgroundProperty, "T.Warn");
        Paint(_bellBadge, Border.BorderBrushProperty, "T.Bg");
        _bell = new Button
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            Content = new Grid { Width = 38, Height = 38, Children = { bellIcon } },
        };
        Paint(_bell, BackgroundProperty, "T.Card");
        Paint(_bell, BorderBrushProperty, "T.Border");
        var bellHost = new Grid { Children = { _bell, _bellBadge } };
        var bellFlyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        bellFlyout.Opening += (_, _) => bellFlyout.Content = BuildBellFlyout(bellFlyout);
        _bell.Flyout = bellFlyout;
        SetTip(_bell, "Needs you");
        right.Children.Add(bellHost);

        _topAccountName = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis };
        _topAccountInitial = new TextBlock { Text = "A", FontSize = 14, FontWeight = FontWeight.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Paint(_topAccountInitial, TextBlock.ForegroundProperty, "T.AccentInk");
        _topAccount = new Button
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = _topAccountInitial,
        };
        Paint(_topAccount, BackgroundProperty, "T.AccentSoft");
        _topAccount.Click += OnAccount;
        right.Children.Add(_topAccountName);
        right.Children.Add(_topAccount);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        bar.Children.Add(logo);
        Grid.SetColumn(pills, 1);
        bar.Children.Add(pills);
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        _topBar = new Border { Margin = new Thickness(16, 12, 16, 0), Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(26), BorderThickness = new Thickness(1), Child = bar };
        Paint(_topBar, Border.BackgroundProperty, "T.Card");
        Paint(_topBar, Border.BorderBrushProperty, "T.Border");
        Grid.SetColumnSpan(_topBar, 2);
        Root.Children.Add(_topBar);

        // ---- the version, bottom left
        var version = new TextBlock { Text = PairnetsInfo.DisplayVersion, FontSize = 11.5, Margin = new Thickness(24, 2, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        Paint(version, TextBlock.ForegroundProperty, "T.Muted");
        Grid.SetRow(version, 2);
        Grid.SetColumnSpan(version, 2);
        Root.Children.Add(version);

        // ---- the page header: the title's "Developer beta" pill and the nest pill
        var beta = new Border { Padding = new Thickness(10, 3), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 4, 0, 0) };
        Paint(beta, Border.BackgroundProperty, "T.AccentSoft");
        var betaText = new TextBlock { Text = BetaTag.IsVisible ? BetaTagText.Text : "Developer beta", FontSize = 12, FontWeight = FontWeight.Bold };
        Paint(betaText, TextBlock.ForegroundProperty, "T.AccentInk");
        beta.Child = betaText;
        if (PageTitle.Parent is StackPanel titleHost)
        {
            var index = titleHost.Children.IndexOf(PageTitle);
            titleHost.Children.RemoveAt(index);
            titleHost.Children.Insert(index, new StackPanel { Orientation = Orientation.Horizontal, Children = { PageTitle, beta } });
        }
        PageNest.IsVisible = false;

        _nestPillText = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        var chevron = new LineIcon { Width = 12, Height = 12, StrokeThickness = 2.4, Data = Visuals.Resource<Geometry>("I.ChevronDown") };
        Paint(chevron, LineIcon.StrokeProperty, "T.Muted");
        _nestPill = new Button
        {
            Height = 36, CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 0), IsVisible = false, BorderThickness = new Thickness(1),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _nestPillText, chevron } },
        };
        Paint(_nestPill, BackgroundProperty, "T.Card");
        Paint(_nestPill, BorderBrushProperty, "T.Border");
        var nestMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        nestMenu.Opening += (_, _) => FillNestMenu(nestMenu);
        _nestPill.Flyout = nestMenu;
        _nestPill.HorizontalAlignment = HorizontalAlignment.Left;
        _nestPill.Margin = new Thickness(0, 8, 0, 0); // under the title, so the header's buttons never squeeze it
        (PageTitle.Parent?.Parent as StackPanel)?.Children.Add(_nestPill);

        DrawTopBar();
    }

    private static Button MakePill(string text)
    {
        var button = new Button
        {
            Content = text, Height = 36, CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 0), Margin = new Thickness(0, 2, 6, 2),
            FontSize = 13.5, FontWeight = FontWeight.SemiBold, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Paints the bar from the window's current state (the page, the attention count, the update, the nests, the account).</summary>
    private void DrawTopBar()
    {
        if (_topBar is null)
            return;
        var current = Page switch { MainPage.History => MainPage.Files, MainPage.Attention or MainPage.Account => (MainPage?)null, var p => p };
        foreach (var (page, pill) in _topPills)
        {
            var active = page == current;
            Paint(pill, BackgroundProperty, active ? "T.AccentButton" : "T.Card");
            Paint(pill, BorderBrushProperty, active ? "T.AccentButton" : "T.Border");
            if (active)
                pill.Foreground = Brushes.White;
            else
                Paint(pill, ForegroundProperty, "T.Text");
            pill.Classes.Set("current", active);
        }

        if (_sideModel is { } m)
        {
            var count = m.Tiles.AttentionCount;
            _bellBadge!.IsVisible = count > 0;
            _bellBadgeText!.Text = m.Tiles.AttentionNote;
            SetTip(_bell!, count > 0 ? $"Needs you, {count}" : "Needs you");
            _topAccountInitial!.Text = m.AccountInitial;
            var name = m.AccountLabel.StartsWith("Account: ", StringComparison.Ordinal) ? m.AccountLabel["Account: ".Length..] : string.Empty;
            var at = name.IndexOf('@');
            _topAccountName!.Text = at > 0 ? name[..at] : name;
            _topAccountName.IsVisible = name.Length > 0;
            SetTip(_topAccount!, m.AccountLabel);
        }

        _updatePill!.IsVisible = _update.Visible;
        if (_update.Visible)
        {
            _updatePillText!.Text = _update.ShowBar ? "Updating " + _update.PercentText : _update.HasButton ? UpdateButton.Content?.ToString() ?? "Update" : "Update";
            SetTip(_updatePill, _update.Title);
        }

        var nests = _nests;
        _nestPill!.IsVisible = nests is { Visible: true };
        if (nests is { Visible: true })
        {
            _nestPillText!.Text = "Nest: " + (nests.AllSelected || nests.Selected is null ? "All nests" : nests.Selected.Title);
            SetTip(_nestPill, "Switch nest");
        }
    }

    // ------------------------------------------------------------------ the bell and the update pill

    private Control BuildBellFlyout(Flyout flyout)
    {
        var stack = new StackPanel { Width = 360, Spacing = 8 };
        var head = new TextBlock { Text = "Needs you", FontSize = 22, FontWeight = FontWeight.Bold, FontFamily = Visuals.DisplayFamily() };
        stack.Children.Add(head);
        if (_attention.Count == 0)
        {
            var none = new TextBlock { Text = "Nothing needs you. Everything is in order.", FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Paint(none, TextBlock.ForegroundProperty, "T.Muted");
            stack.Children.Add(none);
        }
        foreach (var item in _attention.Take(5))
        {
            var title = new TextBlock { Text = item.Title, FontSize = 13, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = item.Detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            Paint(detail, TextBlock.ForegroundProperty, "T.Muted");
            var review = new Button { Content = "Review", Height = 32, Padding = new Thickness(14, 0), FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            review.Click += (_, _) =>
            {
                flyout.Hide();
                Navigate(MainPage.Attention);
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } });
            Grid.SetColumn(review, 1);
            row.Children.Add(review);
            stack.Children.Add(row);
        }
        if (_attention.Count > 5)
        {
            var more = new TextBlock { Text = $"and {_attention.Count - 5} more", FontSize = 12 };
            Paint(more, TextBlock.ForegroundProperty, "T.Muted");
            stack.Children.Add(more);
        }
        if (_attention.Count > 0)
        {
            var all = new Button { Content = "Open Needs you", HorizontalAlignment = HorizontalAlignment.Left, Height = 32, Padding = new Thickness(14, 0), Classes = { "subtle" } };
            all.Click += (_, _) =>
            {
                flyout.Hide();
                Navigate(MainPage.Attention);
            };
            stack.Children.Add(all);
        }
        return stack;
    }

    private Control BuildUpdateFlyout(Flyout flyout)
    {
        var stack = new StackPanel { Width = 320, Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = _update.Title, FontSize = 14, FontWeight = FontWeight.ExtraBold, TextWrapping = TextWrapping.Wrap });
        if (_update.ShowsReason)
        {
            var reason = new TextBlock { Text = UpdateReason.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Paint(reason, TextBlock.ForegroundProperty, "T.Muted");
            stack.Children.Add(reason);
        }
        if (_update.ShowBar)
            stack.Children.Add(new ProgressBar { Classes = { "thin" }, Minimum = 0, Maximum = 100, Value = _update.Percent ?? 0 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (_update.HasButton)
        {
            var go = new Button { Content = UpdateButton.Content, Classes = { "accent", "small" } };
            go.Click += (s, e) =>
            {
                flyout.Hide();
                OnUpdateNow(s, e);
            };
            buttons.Children.Add(go);
        }
        if (!_update.ShowBar)
        {
            var later = new Button { Content = "Later", Classes = { "subtle", "small" } };
            later.Click += (s, e) =>
            {
                flyout.Hide();
                OnUpdateLater(s, e);
            };
            buttons.Children.Add(later);
        }
        if (buttons.Children.Count > 0)
            stack.Children.Add(buttons);
        return stack;
    }

    // ------------------------------------------------------------------ the nest pill

    private void FillNestMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        if (_nests is not { Visible: true } nests)
            return;
        var all = new MenuItem { Header = "All nests" + (nests.AllSelected ? "  ✓" : string.Empty) };
        all.Click += (_, _) => _actions?.SelectNest(null);
        menu.Items.Add(all);
        foreach (var row in nests.Rows)
        {
            var id = row.Id;
            var item = new MenuItem { Header = row.Title + (row.Selected && !nests.AllSelected ? "  ✓" : string.Empty) };
            item.Click += (_, _) => _actions?.SelectNest(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add a nest" };
        add.Click += (_, _) => _actions?.AddNest();
        menu.Items.Add(add);
    }
}
