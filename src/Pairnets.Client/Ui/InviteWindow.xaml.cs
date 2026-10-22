using System.Windows;
using System.Windows.Controls;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Invite a friend…" (friend invites, 1.0.61.2): the whole nest or chosen top-level folders (this computer's folders,
/// with their sizes), "View only" (the default) or "Can change". "Continue in your browser" opens the account page's
/// invite form filled in with these (<see cref="Relay.InviteUrl"/>); the website sends the email or gives the link,
/// because only a signed-in browser may make an invite. The Mac/Linux app has the same window.
/// </summary>
public partial class InviteWindow : Window
{
    private readonly string _nestId;
    private readonly string? _folder;
    private readonly Action<string> _open;
    private readonly Uri? _service;

    /// <param name="nestTitle">"Home nest".</param>
    /// <param name="nestId">The nest's id at Pairnets ("nst_…").</param>
    /// <param name="folder">The nest's folder on this computer, whose top-level folders are offered.</param>
    /// <param name="sealedNest">An encrypted nest: it can only be shared whole.</param>
    /// <param name="open">Opens a link in the browser.</param>
    /// <param name="service">The Pairnets service (tests use a stand-in).</param>
    public InviteWindow(string nestTitle, string nestId, string? folder, bool sealedNest, Action<string> open, Uri? service = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _nestId = nestId;
        _folder = folder;
        _open = open;
        _service = service;
        Heading.Text = "Invite a friend to " + nestTitle;
        if (sealedNest)
        {
            FoldersChoice.IsEnabled = false;
            SealedNote.Visibility = Visibility.Visible;
        }
        Loaded += async (_, _) => await LoadFoldersAsync();
    }

    /// <summary>The link "Continue in your browser" opened, or null.</summary>
    public string? OpenedLink { get; private set; }

    /// <summary>True once this computer's folders are listed.</summary>
    public bool FoldersLoaded { get; private set; }

    public bool ProblemShown => Problem.Visibility == Visibility.Visible;

    /// <summary>The folders ticked now.</summary>
    public IReadOnlyList<string> ChosenFolders => FolderList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

    private async Task LoadFoldersAsync()
    {
        var folder = _folder;
        var found = await Task.Run(() => InviteChoices.LocalFolders(folder));
        _sizes = found.ToDictionary(f => f.Name, f => f.Bytes, StringComparer.Ordinal);
        FolderList.Children.Clear();
        foreach (var (name, bytes) in found)
        {
            var box = new CheckBox { Tag = name, Style = (Style)FindResource("FolderRow"), Content = Row(name, InviteChoices.SizeText(bytes)) };
            box.Checked += (_, _) => ShowChosen();
            box.Unchecked += (_, _) => ShowChosen();
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            FolderList.Children.Add(box);
        }
        FolderNote.Text = found.Count == 0
            ? "There are no folders here yet. Share the whole nest, or make a folder first."
            : "Everything inside a folder comes with it.";
        AllSize.Text = InviteChoices.SizeText(found.Sum(f => f.Bytes));
        AllFolders.Visibility = AllLine.Visibility = found.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowChosen();
        FoldersLoaded = true;
    }

    private Dictionary<string, long> _sizes = [];

    private IEnumerable<CheckBox> FolderBoxes => FolderList.Children.OfType<CheckBox>();

    /// <summary>A folder row's words: its name (cut short with "…" when long) and its size on the right.</summary>
    private static Grid Row(string name, string size)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var bytes = new TextBlock { Text = size, FontWeight = FontWeights.Normal, FontSize = 12.5, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        bytes.SetResourceReference(TextBlock.ForegroundProperty, "T.Muted");
        Grid.SetColumn(bytes, 1);
        row.Children.Add(bytes);
        return row;
    }

    /// <summary>"2 of 4 chosen · 2.59 GB" over the list, and "All folders" ticked, empty or with a dash.</summary>
    private void ShowChosen()
    {
        var chosen = FolderBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        ChosenSummary.Text = _sizes.Count == 0 ? "" : InviteChoices.ChosenSummary(chosen.Count, _sizes.Count, chosen.Sum(n => _sizes[n]));
        AllFolders.IsChecked = InviteChoices.AllTicked(chosen.Count, _sizes.Count);
    }

    /// <summary>"All folders": ticks every folder, or clears them all when every one is ticked already.</summary>
    private void OnAllFolders(object sender, RoutedEventArgs e)
    {
        var tickAll = FolderBoxes.Any(b => b.IsChecked != true);
        foreach (var box in FolderBoxes)
            box.IsChecked = tickAll;
        ShowChosen();
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (FolderBox is null)
            return; // while the window is being built
        FolderBox.Visibility = FoldersChoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Problem.Visibility = Visibility.Collapsed;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        var whole = WholeChoice.IsChecked == true;
        IReadOnlyList<string> folders = whole ? [] : ChosenFolders;
        if (InviteChoices.Problem(whole, folders) is { } problem)
        {
            Problem.Text = problem;
            Problem.Visibility = Visibility.Visible;
            return;
        }
        OpenedLink = Relay.InviteUrl(_nestId, whole ? null : folders, canChange: EditChoice.IsChecked == true, _service);
        _open(OpenedLink);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
