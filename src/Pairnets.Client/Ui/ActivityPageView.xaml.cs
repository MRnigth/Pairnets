using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// The Activity page: filter pills with counts, a search box, and a list of day headings, files and folder rows
/// (a folder row opens to its first files and "Show all"). What is shown comes from <see cref="ActivityView"/>.
/// </summary>
public partial class ActivityPageView : UserControl
{
    private readonly bool _ready;
    private readonly HashSet<string> _open = [];
    private readonly HashSet<string> _all = [];
    private IReadOnlyList<ActivityItem> _items = [];
    private IReadOnlyList<object> _shown = [];
    private ActivityFilter _filter = ActivityFilter.All;
    private string? _drawnFor;

    public ActivityPageView()
    {
        InitializeComponent();
        Search.TextChanged += Rebuild;
        FilterBar.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                FitFilterBar();
        };
        _ready = true;
        EmptyText.Text = "Put a file in your folder and it shows up here as soon as it is safe on your nest.";
        Rebuild();
    }

    /// <summary>What the page asks of the main window.</summary>
    public IPageHost? Host { get; set; }

    /// <summary>The synced folder, named in the empty state.</summary>
    public string? Folder
    {
        get => _folder;
        set
        {
            _folder = value;
            EmptyText.Text = value is { Length: > 0 }
                ? $"Put a file in {value} and it shows up here as soon as it is safe on your nest."
                : "Put a file in your folder and it shows up here as soon as it is safe on your nest.";
        }
    }

    private string? _folder;

    /// <summary>The chosen filter button.</summary>
    public ActivityFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            (value switch
            {
                ActivityFilter.Uploaded => FilterUploaded,
                ActivityFilter.Downloaded => FilterDownloaded,
                ActivityFilter.Deleted => FilterDeleted,
                ActivityFilter.Conflicts => FilterConflicts,
                _ => FilterAll,
            }).IsChecked = true;
            Rebuild();
        }
    }

    public string SearchText
    {
        get => Search.Text;
        set => Search.Text = value;
    }

    /// <summary>Everything the list shows right now: day headings, files and folder rows.</summary>
    public IReadOnlyList<object> ShownRows => _shown;

    /// <summary>The number on a filter button ("Uploaded 40").</summary>
    public string CountOf(ActivityFilter filter) => (filter switch
    {
        ActivityFilter.Uploaded => CountUploaded,
        ActivityFilter.Downloaded => CountDownloaded,
        ActivityFilter.Deleted => CountDeleted,
        ActivityFilter.Conflicts => CountConflicts,
        _ => CountAll,
    }).Text;

    /// <summary>True while the page shows the "nothing has synced yet" picture instead of a list.</summary>
    public bool ShowsEmptyState => EmptyState.Visibility == Visibility.Visible;

    /// <summary>The smaller message inside the list when a filter or the search matches nothing (empty otherwise).</summary>
    public string NoMatchMessage => NoMatch.Visibility == Visibility.Visible ? NoMatchText.Text : string.Empty;

    /// <summary>Opens the first folder row of that folder (to show its first files).</summary>
    public void OpenFolder(string folder)
    {
        foreach (var row in _shown.OfType<ActivityFolder>().Where(f => f.Folder == folder || f.Name == folder).Take(1))
            _open.Add($"{row.Kind}|{row.Folder}|{row.Time.ToLocalTime():yyyy-MM-dd}");
        _drawnFor = null;
        Rebuild();
    }

    /// <summary>The folder rows currently opened.</summary>
    public IReadOnlyCollection<string> OpenFolders => _open;

    /// <summary>New activity arrived (or the page was opened): draw it again where something changed.</summary>
    public void Show(IReadOnlyList<ActivityItem> items)
    {
        _items = items;
        Rebuild();
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return; // Checked also fires while the page is read from its XAML
        _filter = sender == FilterUploaded ? ActivityFilter.Uploaded
            : sender == FilterDownloaded ? ActivityFilter.Downloaded
            : sender == FilterDeleted ? ActivityFilter.Deleted
            : sender == FilterConflicts ? ActivityFilter.Conflicts
            : ActivityFilter.All;
        Rebuild();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => Host?.Actions?.OpenFolder();

    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// The search box beside the filter buttons when both fit, else on its own line under them (in a narrow window the
    /// buttons were pushed out of sight).
    /// </summary>
    private void FitFilterBar()
    {
        var width = FilterBar.ActualWidth;
        if (width <= 0)
            return;
        var filters = 0.0;
        foreach (UIElement pill in Filters.Children)
        {
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            filters += pill.DesiredSize.Width;
        }
        var below = PageLayout.SearchGoesBelow(filters, width);
        Grid.SetRow(Search, below ? 1 : 0);
        Grid.SetColumn(Search, below ? 0 : 1);
        Grid.SetColumnSpan(Search, below ? 2 : 1);
        Search.Width = below ? double.NaN : PageLayout.SearchWidth;
        Search.Margin = below ? new Thickness(0, 10, 0, 0) : new Thickness(PageLayout.SearchGap, 0, 0, 0);
    }

    private void Rebuild()
    {
        if (!_ready)
            return;
        var search = Search.Text;
        var counts = ActivityView.Counts(_items, search);
        CountAll.Text = Format.Count(counts.All);
        CountUploaded.Text = Format.Count(counts.Uploaded);
        CountDownloaded.Text = Format.Count(counts.Downloaded);
        CountDeleted.Text = Format.Count(counts.Deleted);
        CountConflicts.Text = Format.Count(counts.Conflicts);
        FitFilterBar();

        var none = _items.Count == 0;
        FilterBar.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = none ? Visibility.Visible : Visibility.Collapsed;

        var rows = none ? [] : ActivityView.Rows(_items, _filter, search, DateTimeOffset.Now);
        ListScroll.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Visibility = !none && rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (NoMatch.Visibility == Visibility.Visible)
            NoMatchText.Text = ActivityView.EmptyText(_items, _filter, search);

        // Draw again only when the rows changed, so an open folder row stays as it is while new files arrive.
        var signature = _filter + "|" + search;
        if (rows.Count == _shown.Count && signature == _drawnFor && rows.Zip(_shown).All(p => Equals(p.First, p.Second)))
            return;
        _drawnFor = signature;
        _shown = rows;
        var offset = ListScroll.VerticalOffset;
        ActivityRows.Children.Clear();
        var first = true;
        foreach (var row in rows)
        {
            switch (row)
            {
                case ActivityDay day:
                    ActivityRows.Children.Add(DayHeading(day, first));
                    break;
                case ActivityFolder folder:
                    ActivityRows.Children.Add(FolderRow(folder));
                    break;
                case ActivityItem item:
                    ActivityRows.Children.Add(FileRow(item, indent: false));
                    break;
            }
            first = false;
        }
        if (offset > 0)
        {
            ListScroll.UpdateLayout();
            ListScroll.ScrollToVerticalOffset(offset);
        }
    }

    private static UIElement DayHeading(ActivityDay day, bool first)
    {
        var grid = new Grid { Margin = new Thickness(0, first ? 0 : 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Parts.Text(day.Text.ToUpperInvariant(), 11, "T.TextSoft", FontWeights.Bold);
        var count = Parts.Text(day.CountText, 12, "T.TextSoft");
        Grid.SetColumn(count, 1);
        grid.Children.Add(title);
        grid.Children.Add(count);
        var holder = new Border { Padding = new Thickness(10, first ? 12 : 16, 10, 6), Child = grid, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) };
        holder.SetResourceReference(Border.BorderBrushProperty, "T.Border");
        if (!first)
            holder.Margin = new Thickness(0, 6, 0, 0);
        return holder;
    }

    private (string Fill, string Ink, string Icon) Look(ActivityKind kind) => kind switch
    {
        ActivityKind.Uploaded => ("T.Chip", "T.AccentInk", "I.Up"),
        ActivityKind.Downloaded => ("T.Chip", "T.OkText", "I.Down"),
        ActivityKind.Conflict => ("T.WarnPill", "T.WarnText", "I.Warning"),
        ActivityKind.Blocked => ("T.WarnPill", "T.WarnText", "I.Pause"),
        ActivityKind.DeletedHere or ActivityKind.DeletedOnServer => ("T.Chip", "T.Muted", "I.Trash"),
        ActivityKind.Warning or ActivityKind.Error => ("T.BadPill", "T.BadText", "I.Bang"),
        ActivityKind.Offline => ("T.Chip", "T.Muted", "I.CloudOff"),
        _ => ("T.Chip", "T.Muted", "I.Info"),
    };

    private UIElement FileRow(ActivityItem item, bool indent)
    {
        if (indent)
            return SubFileRow(item);
        var (fill, ink, icon) = Look(item.Kind);
        var grid = new Grid();
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var square = Parts.Square(34, 11, fill, Parts.Icon(icon, 16, ink, 2.2));
        square.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(square);

        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(Parts.Text(item.Primary, 14, "T.Text", FontWeights.SemiBold));
        lines.Children.Add(Parts.Text(item.Secondary, 12, "T.TextSoft"));
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);

        if (ActivityView.NeedsReview(item))
        {
            var review = new Button { Style = (Style)FindResource("PgButton"), Height = 28, Padding = new Thickness(12, 0, 12, 0), FontSize = 12, FontWeight = FontWeights.Bold,
                Content = "Review", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Pg.SetRadius(review, new CornerRadius(9));
            review.SetResourceReference(Control.BorderBrushProperty, "T.WarnBorder");
            review.Click += (_, _) => Host?.Navigate(MainPage.Attention);
            Grid.SetColumn(review, 2);
            grid.Children.Add(review);
        }

        var time = Parts.Text(Parts.Clock(item.Time), 12, "T.TextSoft");
        time.Width = 44;
        time.TextAlignment = TextAlignment.Right;
        time.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(time, 3);
        grid.Children.Add(time);
        grid.Children.Add(Spacer(4));
        return Row(grid, item);
    }

    private static FrameworkElement Spacer(int column)
    {
        var gap = new Border { Width = 30 };
        Grid.SetColumn(gap, column);
        return gap;
    }

    private Border Row(UIElement content, ActivityItem item)
    {
        var row = new Border { Style = (Style)FindResource("Row"), Padding = new Thickness(10, 6, 10, 6), Child = content, DataContext = item, ToolTip = item.TimeText };
        AttachMenu(row, item);
        return row;
    }

    private void AttachMenu(FrameworkElement owner, ActivityItem item)
    {
        if (item.Path is not { } path)
            return;
        var reveal = new MenuItem { Header = "Show in folder" };
        reveal.Click += (_, _) => Host?.Actions?.RevealFile(path);
        var versions = new MenuItem { Header = "Show versions…" };
        versions.Click += (_, _) => Host?.ShowVersionsFor(path);
        owner.ContextMenu = new ContextMenu { Items = { reveal, versions } };
    }

    /// <summary>A file inside an opened folder row: its name and the time.</summary>
    private UIElement SubFileRow(ActivityItem item)
    {
        var grid = new Grid { Height = 26, Background = Brushes.Transparent, DataContext = item, ToolTip = item.TimeText };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Parts.Text(item.Primary, 13));
        var time = Parts.Text(Parts.Clock(item.Time), 12, "T.TextSoft");
        time.Margin = new Thickness(10, 0, 52, 0);
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);
        AttachMenu(grid, item);
        return grid;
    }

    private UIElement FolderRow(ActivityFolder folder)
    {
        var key = $"{folder.Kind}|{folder.Folder}|{folder.Time.ToLocalTime():yyyy-MM-dd}";
        var open = _open.Contains(key);

        var grid = new Grid();
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var square = Parts.Square(34, 11, "T.Chip", Parts.Icon("I.Folder", 16, "T.Folder", 1.9));
        square.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(square);

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var name = Parts.Text(folder.Name, 14, "T.Text", FontWeights.SemiBold);
        name.MaxWidth = 340;
        title.Children.Add(name);
        var chip = Parts.Chip(folder.CountText);
        chip.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(chip);
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(title);
        lines.Children.Add(Parts.Text(folder.Detail, 12, "T.TextSoft"));
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);

        var time = Parts.Text(Parts.Clock(folder.Time), 12, "T.TextSoft");
        time.Width = 44;
        time.TextAlignment = TextAlignment.Right;
        time.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var chevron = Parts.Icon(open ? "I.ChevronUp" : "I.ChevronDown", 16, "T.Muted", 2.2);
        var toggle = new Button { Style = (Style)FindResource("PgButton"), Width = 30, Height = 30, Padding = new Thickness(0), BorderThickness = new Thickness(0), Content = chevron, Margin = new Thickness(0, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand };
        toggle.SetResourceReference(Control.BackgroundProperty, "T.Chip");
        toggle.SetResourceReference(FrameworkElement.TagProperty, "T.SubtleHover");
        Pg.SetRadius(toggle, new CornerRadius(10));
        Grid.SetColumn(toggle, 3);
        grid.Children.Add(toggle);

        var row = new Border { Style = (Style)FindResource("Row"), Padding = new Thickness(10, 6, 10, 6), Child = grid };

        var block = new Border { Margin = new Thickness(56, 0, 0, 6), Padding = new Thickness(14, 0, 0, 0), BorderThickness = new Thickness(2, 0, 0, 0), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        block.SetResourceReference(Border.BorderBrushProperty, "T.Track");
        var files = new StackPanel();
        block.Child = files;

        void Fill()
        {
            files.Children.Clear();
            var all = _all.Contains(key);
            foreach (var item in all ? folder.Items : folder.Items.Take(ActivityFolder.Preview))
                files.Children.Add(FileRow(item, indent: true));
            if (!all && folder.Items.Count > ActivityFolder.Preview)
            {
                var more = new Button { Style = (Style)FindResource("PgLink"), Content = "Show all " + folder.CountText, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 4, 0, 4) };
                more.Click += (_, _) =>
                {
                    _all.Add(key);
                    Fill();
                };
                files.Children.Add(more);
            }
        }

        void Describe(bool isOpen)
        {
            chevron.Data = Visuals.Resource<Geometry>(isOpen ? "I.ChevronUp" : "I.ChevronDown");
            var label = (isOpen ? "Hide the " : "Show the ") + folder.CountText;
            toggle.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(toggle, label);
            System.Windows.Automation.AutomationProperties.SetItemStatus(toggle, isOpen ? "expanded" : "collapsed");
        }

        toggle.Click += (_, _) =>
        {
            var now = block.Visibility != Visibility.Visible;
            if (now)
            {
                _open.Add(key);
                Fill();
            }
            else
            {
                _open.Remove(key);
            }
            block.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
            Describe(now);
        };
        Describe(open);
        if (open)
            Fill();
        return new StackPanel { Children = { row, block } };
    }
}
