using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// "Invite a friend…" (friend invites, 1.0.61.2): the whole nest or chosen top-level folders (this computer's folders,
/// with their sizes), "View only" (the default) or "Can change". "Continue in your browser" opens the account page's
/// invite form filled in with these (<see cref="Relay.InviteUrl"/>); the website sends the email or gives the link,
/// because only a signed-in browser may make an invite. The Windows app has the same window.
/// </summary>
public partial class InviteWindow : Window
{
    private readonly string _nestId;
    private readonly string? _folder;
    private readonly Action<string> _open;
    private readonly Uri? _service;

    public InviteWindow()
        : this("Your nest", "nst_00000000000000000000000000", null, false, _ => { })
    {
    }

    /// <param name="nestTitle">"Home nest".</param>
    /// <param name="nestId">The nest's id at Pairnets ("nst_…").</param>
    /// <param name="folder">The nest's folder on this computer, whose top-level folders are offered.</param>
    /// <param name="sealedNest">An encrypted nest: it can only be shared whole.</param>
    /// <param name="open">Opens a link in the browser.</param>
    /// <param name="service">The Pairnets service (tests use a stand-in).</param>
    public InviteWindow(string nestTitle, string nestId, string? folder, bool sealedNest, Action<string> open, Uri? service = null)
    {
        InitializeComponent();
        _nestId = nestId;
        _folder = folder;
        _open = open;
        _service = service;
        Heading.Text = "Invite a friend to " + nestTitle;
        if (sealedNest)
        {
            FoldersChoice.IsEnabled = false;
            SealedNote.IsVisible = true;
        }
        Opened += async (_, _) => await LoadFoldersAsync();
    }

    /// <summary>The link "Continue in your browser" opened, or null.</summary>
    internal string? OpenedLink { get; private set; }

    /// <summary>True once this computer's folders are listed.</summary>
    internal bool FoldersLoaded { get; private set; }

    internal bool ProblemShown => Problem.IsVisible;

    /// <summary>The folders ticked now.</summary>
    internal IReadOnlyList<string> ChosenFolders => FolderList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();

    internal async Task LoadFoldersAsync()
    {
        var folder = _folder;
        var found = await Task.Run(() => InviteChoices.LocalFolders(folder));
        FolderList.Children.Clear();
        foreach (var (name, bytes) in found)
        {
            var size = new TextBlock { Text = Format.Bytes(bytes), FontSize = 12, Margin = new Avalonia.Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            size.Classes.Add("muted");
            var box = new CheckBox
            {
                Tag = name,
                Margin = new Avalonia.Thickness(0, 8, 0, 0),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = name, FontWeight = FontWeight.SemiBold }, size } },
            };
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            FolderList.Children.Add(box);
        }
        FolderNote.Text = found.Count == 0
            ? "There are no folders here yet. Share the whole nest, or make a folder first."
            : "Tick the folders they get. Everything inside a folder comes with it.";
        FoldersLoaded = true;
    }

    private void OnScopeChanged(object? sender, RoutedEventArgs e)
    {
        if (FolderBox is null)
            return; // while the window is being built
        FolderBox.IsVisible = FoldersChoice.IsChecked == true;
        Problem.IsVisible = false;
    }

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        var whole = WholeChoice.IsChecked == true;
        IReadOnlyList<string> folders = whole ? [] : ChosenFolders;
        if (InviteChoices.Problem(whole, folders) is { } problem)
        {
            Problem.Text = problem;
            Problem.IsVisible = true;
            return;
        }
        OpenedLink = Relay.InviteUrl(_nestId, whole ? null : folders, canChange: EditChoice.IsChecked == true, _service);
        _open(OpenedLink);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
