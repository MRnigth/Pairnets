using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Developer beta look's top bar (docs/DECISIONS.md, "Developer beta look"): it replaces the left side panel. Logo, a pill
/// for each page, the update pill (only while the old update card would show), the bell with the "Needs you" count, and the
/// account. Everything here is built in code and only when <see cref="DevBetaLook.On"/>; in every other build the left side is
/// exactly as before and none of this runs.
/// </summary>
public partial class MainWindow
{
    private const double BellPopupWidth = 360;

    private readonly Dictionary<MainPage, Button> _pills = [];
    private Button? _bell;
    private Border? _bellBadge;
    private TextBlock? _bellBadgeText;
    private Popup? _bellPopup;
    private StackPanel? _bellList;
    private Border? _updatePill;
    private TextBlock? _updateText;
    private TextBlock? _updatePercent;
    private Button? _updateButton;
    private Button? _updateLater;
    private TextBlock? _accountName;
    private TextBlock? _accountInitialTop;
    private Button? _accountButtonTop;
    private Button? _nestPill;
    private TextBlock? _nestPillText;
    private IReadOnlyList<AttentionItem> _attentionItems = [];

    /// <summary>True when this window draws the top bar instead of the left side.</summary>
    internal bool TopBarOn => DevBetaLook.On;

    /// <summary>The pages the pills open, in order (the Devices page is called Computers).</summary>
    internal static readonly (MainPage Page, string Label)[] TopPages =
    [
        (MainPage.Overview, "Overview"), (MainPage.Activity, "Activity"), (MainPage.Files, "Files"),
        (MainPage.Devices, "Computers"), (MainPage.Settings, "Settings"),
    ];

    /// <summary>The pills, bell and update pill, for the checks.</summary>
    internal IReadOnlyDictionary<MainPage, Button> TopPills => _pills;

    internal Button? Bell => _bell;

    internal string BellBadgeText => _bellBadgeText?.Text ?? string.Empty;

    internal bool BellBadgeShown => _bellBadge?.Visibility == Visibility.Visible;

    internal bool UpdatePillShown => _updatePill?.Visibility == Visibility.Visible;

    internal Popup? BellPopup => _bellPopup;

    private void InitTopBar()
    {
        // The left side stays in the file (stable builds use it) but never shows.
        SideColumn.Width = new GridLength(0);
        SideOpen.Visibility = Visibility.Collapsed;
        SideDock.Visibility = Visibility.Collapsed;
        DevTopBar.Visibility = Visibility.Visible;
        DevVersion.Visibility = Visibility.Visible;
        DevVersion.Text = "Version " + PairnetsInfo.DisplayVersion;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top, Source = AppIcons.Large };
        System.Windows.Automation.AutomationProperties.SetName(logo, "Pairnets");
        grid.Children.Add(logo);

        // Pills wrap onto a second line when the window gets narrow instead of clipping.
        var pills = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
        foreach (var (page, label) in TopPages)
        {
            var pill = Pill(label);
            var target = page;
            pill.Click += (_, _) => OpenFromTopBar(target);
            _pills[page] = pill;
            pills.Children.Add(pill);
        }
        Grid.SetColumn(pills, 1);
        grid.Children.Add(pills);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(BuildUpdatePill());
        right.Children.Add(BuildBell());
        right.Children.Add(BuildAccount());
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        DevTopBar.Child = grid;

        BuildHeaderPills();
        DrawTopBar(null);
    }

    private Button Pill(string label)
    {
        var button = new Button
        {
            Style = (Style)FindResource("RoundIconButton"),
            Content = label,
            Height = 40,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(0, 1, 8, 3),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        KeepPillShape(button);
        return button;
    }

    /// <summary>The round style's large radius turns a wide button into an oval: half the height keeps the sides straight.</summary>
    private static void KeepPillShape(Button button) => button.Loaded += (_, _) =>
    {
        if (button.Template.FindName("bd", button) is Border border)
            border.CornerRadius = new CornerRadius(button.Height / 2);
    };

    private void OpenFromTopBar(MainPage page)
    {
        if (page == MainPage.Settings && _actions is not null)
        {
            Navigate(MainPage.Settings);
            _actions.ShowSettings(); // comes back through ShowSettingsPage with a fresh form, as the side tile does
            return;
        }
        Navigate(page);
    }

    // ------------------------------------------------------------------ update pill

    private Border BuildUpdatePill()
    {
        _updateText = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };
        _updateText.SetResourceReference(TextBlock.ForegroundProperty, "T.AccentInk");
        _updatePercent = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _updatePercent.SetResourceReference(TextBlock.ForegroundProperty, "T.AccentInk");
        var icon = Parts.Icon("I.Download", 16, "T.AccentInk", 2.2);
        icon.Margin = new Thickness(0, 0, 8, 0);
        _updateButton = new Button { Style = (Style)FindResource("AccentButton"), Height = 28, MinHeight = 28, Padding = new Thickness(12, 0, 12, 0), FontSize = 12, Margin = new Thickness(10, 0, 0, 0) };
        _updateButton.Click += OnUpdateNow;
        _updateLater = new Button { Style = (Style)FindResource("RoundIconButton"), Width = 26, Height = 26, MinWidth = 26, MinHeight = 26, Margin = new Thickness(4, 0, 0, 0), Background = Brushes.Transparent, ToolTip = "Later" };
        _updateLater.Content = Parts.Icon("I.Close", 12, "T.Muted", 2.4);
        System.Windows.Automation.AutomationProperties.SetName(_updateLater, "Later");
        _updateLater.Click += OnUpdateLater;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { icon, _updateText, _updatePercent, _updateButton, _updateLater } };
        _updatePill = new Border { Height = 40, CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 0, 8, 0), Margin = new Thickness(0, 1, 10, 0), Child = row, Visibility = Visibility.Collapsed };
        _updatePill.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
        return _updatePill;
    }

    /// <summary>Shows the update pill from the same model the old update card uses (title, button, percent, reason).</summary>
    private void DrawTopUpdate()
    {
        if (_updatePill is null)
            return;
        var u = _update;
        _updatePill.Visibility = Show(u.Visible);
        if (!u.Visible)
            return;
        var busy = u.Kind == UpdateCardKind.Downloading;
        _updateText!.Text = u.Title;
        _updatePercent!.Text = u.PercentText;
        _updatePercent.Visibility = Show(busy);
        _updateButton!.Content = u.Button;
        _updateButton.Visibility = Show(u.HasButton);
        _updateLater!.Visibility = Show(!busy);
        // The reason an update was not installed (or must be downloaded by hand) is in the tooltip and read out with the pill.
        _updatePill.ToolTip = u.Detail.Length > 0 ? u.Detail : null;
        System.Windows.Automation.AutomationProperties.SetName(_updatePill, u.ShowsReason ? u.Title + ". " + u.Detail : u.Title);
    }

    // ------------------------------------------------------------------ bell

    private UIElement BuildBell()
    {
        _bell = new Button { Style = (Style)FindResource("RoundIconButton"), Width = 42, Height = 42, MinWidth = 42, MinHeight = 42, Margin = new Thickness(0, 0, 0, 0) };
        _bell.SetResourceReference(BackgroundProperty, "T.Card");
        _bell.Content = Parts.Icon("I.Bell", 20, "T.Text", 1.8);
        _bell.Click += (_, _) => ToggleBell();
        System.Windows.Automation.AutomationProperties.SetName(_bell, "Needs you");

        _bellBadgeText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _bellBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "T.NeedsActiveText");
        _bellBadge = new Border
        {
            MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -3, 0),
            IsHitTestVisible = false, Child = _bellBadgeText, Visibility = Visibility.Collapsed,
        };
        _bellBadge.SetResourceReference(Border.BackgroundProperty, "T.NeedsActive");
        _bellBadge.SetResourceReference(Border.BorderBrushProperty, "T.Bg");

        _bellList = new StackPanel();
        var card = new Border { Width = BellPopupWidth, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), Padding = new Thickness(14), Child = _bellList };
        card.SetResourceReference(Border.BackgroundProperty, "T.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        _bellPopup = new Popup
        {
            PlacementTarget = _bell,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = 42 - BellPopupWidth - 12,
            VerticalOffset = 2,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border { Padding = new Thickness(12, 0, 12, 28), Child = card },
        };
        return new Grid { Margin = new Thickness(0, 1, 12, 0), Children = { _bell, _bellBadge } };
    }

    private void ToggleBell()
    {
        if (_bellPopup is null)
            return;
        FillBell();
        _bellPopup.IsOpen = !_bellPopup.IsOpen;
    }

    /// <summary>The list under the bell: what the Needs you page lists, each with a Review button that opens that page.</summary>
    private void FillBell()
    {
        if (_bellList is null)
            return;
        _bellList.Children.Clear();
        var head = new Grid { Margin = new Thickness(4, 2, 4, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Parts.Text("NEEDS YOU", 17, "T.Text", FontWeights.Bold);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "F.Display");
        var count = Parts.Text(_attentionItems.Count == 0 ? "All clear" : _attentionItems.Count == 1 ? "1 item" : _attentionItems.Count + " items", 12, "T.Muted");
        Grid.SetColumn(count, 1);
        head.Children.Add(title);
        head.Children.Add(count);
        _bellList.Children.Add(head);

        if (_attentionItems.Count == 0)
        {
            var calm = Parts.Text("Nothing needs you right now.", 13, "T.Muted", trim: false);
            calm.TextWrapping = TextWrapping.Wrap;
            calm.Margin = new Thickness(4, 4, 4, 8);
            _bellList.Children.Add(calm);
        }
        foreach (var item in _attentionItems.Take(6))
            _bellList.Children.Add(BellRow(item));
        if (_attentionItems.Count > 6)
        {
            var more = Parts.Text($"and {_attentionItems.Count - 6} more", 12, "T.Muted");
            more.Margin = new Thickness(4, 8, 4, 0);
            _bellList.Children.Add(more);
        }
        var all = new Button { Style = (Style)FindResource("PgLink"), Content = "Open Needs you", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        all.Click += (_, _) => OpenNeedsYou();
        _bellList.Children.Add(all);
    }

    private UIElement BellRow(AttentionItem item)
    {
        var (fill, ink, icon) = item.Kind switch
        {
            AttentionKind.Join => ("T.AccentSoft", "T.AccentInk", "I.JoinPc"),
            AttentionKind.Rename => ("T.AccentSoft", "T.AccentInk", "I.Rename"),
            AttentionKind.Shared => ("T.AccentSoft", "T.AccentInk", "I.Nest"),
            _ => ("T.WarnPill", "T.WarnText", "I.Warning"),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var square = Parts.Square(34, 10, fill, Parts.Icon(icon, 17, ink, 1.9));
        square.Margin = new Thickness(0, 0, 10, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Parts.Text(item.Title, 13, "T.Text", FontWeights.SemiBold));
        words.Children.Add(Parts.Text(item.Detail, 11, "T.Muted"));
        Grid.SetColumn(words, 1);
        var review = new Button { Style = (Style)FindResource("PgSmallButton"), Content = "Review", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(review, "Review " + item.Title);
        review.Click += (_, _) => OpenNeedsYou();
        Grid.SetColumn(review, 2);
        grid.Children.Add(square);
        grid.Children.Add(words);
        grid.Children.Add(review);
        var row = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0), Child = grid };
        row.SetResourceReference(Border.BackgroundProperty, "T.Chip");
        return row;
    }

    private void OpenNeedsYou()
    {
        if (_bellPopup is not null)
            _bellPopup.IsOpen = false;
        Navigate(MainPage.Attention);
    }

    // ------------------------------------------------------------------ account

    private UIElement BuildAccount()
    {
        _accountName = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        _accountName.SetResourceReference(TextBlock.ForegroundProperty, "T.Text");
        _accountInitialTop = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _accountInitialTop.SetResourceReference(TextBlock.FontFamilyProperty, "F.Display");
        _accountInitialTop.SetResourceReference(TextBlock.ForegroundProperty, "T.Accent");
        var button = new Button { Style = (Style)FindResource("RoundIconButton"), Width = 42, Height = 42, MinWidth = 42, MinHeight = 42, Content = _accountInitialTop };
        button.SetResourceReference(BackgroundProperty, "T.Card");
        button.Click += OnAccount;
        System.Windows.Automation.AutomationProperties.SetName(button, "Account");
        _accountButtonTop = button;
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0), Children = { _accountName, button } };
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Redraws the pills (the open page), the bell, the account and the update pill (called with every side refresh).</summary>
    private void DrawTopBar(SidePanelModel? m)
    {
        if (!TopBarOn || DevTopBar.Child is null)
            return;
        foreach (var (page, pill) in _pills)
        {
            var current = page == Page || (page == MainPage.Files && Page == MainPage.History);
            pill.SetResourceReference(BackgroundProperty, current ? "T.AccentButton" : "T.Card");
            pill.SetResourceReference(TagProperty, current ? "T.AccentButtonHover" : "T.Hover");
            if (current)
                pill.Foreground = Brushes.White;
            else
                pill.SetResourceReference(ForegroundProperty, "T.Text");
            System.Windows.Automation.AutomationProperties.SetItemStatus(pill, current ? "Current page" : string.Empty);
        }
        DrawTopUpdate();
        if (m is null)
            return;
        var alert = m.Tiles.AttentionAlert;
        _bellBadge!.Visibility = Show(alert);
        _bellBadgeText!.Text = m.Tiles.AttentionNote;
        var label = alert ? $"Needs you, {m.Tiles.AttentionCount}" : "Needs you";
        _bell!.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(_bell, label);
        _accountInitialTop!.Text = m.AccountInitial;
        var email = _status.AccountEmail;
        _accountName!.Text = string.IsNullOrWhiteSpace(email) ? "Account" : email.Split('@')[0];
        _accountButtonTop!.ToolTip = m.AccountLabel;
        System.Windows.Automation.AutomationProperties.SetName(_accountButtonTop, m.AccountLabel);
    }

    // ------------------------------------------------------------------ the page header: Developer beta pill and the nest pill

    private void BuildHeaderPills()
    {
        if (PageTitle.Parent is not StackPanel column)
            return;
        column.Children.Remove(PageTitle);
        PageTitle.VerticalAlignment = VerticalAlignment.Center;
        var tag = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        tag.SetResourceReference(Border.BackgroundProperty, "T.AccentSoft");
        tag.SetResourceReference(Border.BorderBrushProperty, "T.Accent");
        tag.Child = Parts.Text(PairnetsInfo.ThisRelease.IsBeta ? ReleaseChannel.DisplayName(PairnetsInfo.Channel) : "Developer beta", 12, "T.AccentInk", FontWeights.Bold);

        _nestPillText = Parts.Text(string.Empty, 12, "T.Text", FontWeights.SemiBold);
        _nestPill = new Button { Style = (Style)FindResource("RoundIconButton"), Height = 32, MinHeight = 32, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(10, 0, 0, 0), Content = _nestPillText, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        _nestPill.SetResourceReference(BackgroundProperty, "T.Card");
        _nestPill.Click += (_, _) => OpenNestMenu();
        KeepPillShape(_nestPill);
        System.Windows.Automation.AutomationProperties.SetName(_nestPill, "Nest");

        column.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { PageTitle, tag, _nestPill } });
    }

    /// <summary>"Nest: soro-fag" beside the title while there are several nests; it opens the same nests as the old side list.</summary>
    private void DrawTopNest()
    {
        if (_nestPill is null || _nestPillText is null)
            return;
        _nestPill.Visibility = Show(_nests is { Visible: true });
        if (_nests is not { Visible: true } nests)
            return;
        var name = nests.AllSelected || nests.Selected is null ? "All nests" : nests.Selected.Title;
        _nestPillText.Text = "Nest: " + name;
        _nestPill.ToolTip = "Switch nest";
    }

    private void OpenNestMenu()
    {
        if (_nests is not { Visible: true } m || _nestPill is null)
            return;
        var menu = new ContextMenu { PlacementTarget = _nestPill, Placement = PlacementMode.Bottom };
        var all = new MenuItem { Header = "All nests", FontWeight = m.AllSelected ? FontWeights.Bold : FontWeights.Normal };
        all.Click += (_, _) => _actions?.SelectNest(null);
        menu.Items.Add(all);
        foreach (var row in m.Rows)
        {
            var id = row.Id;
            var item = new MenuItem { Header = row.Title, ToolTip = row.Tooltip, FontWeight = row.Selected && !m.AllSelected ? FontWeights.Bold : FontWeights.Normal };
            item.Click += (_, _) => _actions?.SelectNest(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add a nest" };
        add.Click += (_, _) => _actions?.AddNest();
        menu.Items.Add(add);
        menu.IsOpen = true;
    }
}
