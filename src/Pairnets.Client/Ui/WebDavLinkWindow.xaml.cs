using System.Windows;
using System.Windows.Controls;
using Pairnets.Client.Themes;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Make a link…" under Files in Finder or Explorer: a name, the whole nest or chosen top-level folders, "View only" (the
/// default) or "Can change"; then the nest makes the link and the window shows its address, username and password with
/// Copy buttons. The password is shown once and lives only in this window (never in a log, state.db or the settings). The
/// Mac/Linux app has the same window.
/// </summary>
public partial class WebDavLinkWindow : Window
{
    private readonly string _address;
    private readonly Func<string, bool, IReadOnlyList<string>?, Task<(NewWebDavLink? Link, string? Error)>> _make;
    private readonly Func<string, bool> _copy;

    /// <param name="address">What to type in Finder or Explorer (<see cref="WebDavSection.Address"/>).</param>
    /// <param name="folders">The nest's top-level folders, for "Chosen folders".</param>
    /// <param name="make">Asks the nest to make the link (name, can change, folders or null for the whole nest).</param>
    /// <param name="copy">Puts text on the clipboard; false when another program holds it.</param>
    public WebDavLinkWindow(string address, IReadOnlyList<string> folders, Func<string, bool, IReadOnlyList<string>?, Task<(NewWebDavLink? Link, string? Error)>> make, Func<string, bool> copy)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _address = address;
        _make = make;
        _copy = copy;
        FolderNote.Text = folders.Count == 0 ? "There are no folders on the nest yet. Use the whole nest, or make a folder first." : "Everything inside a folder comes with it.";
        foreach (var name in folders)
        {
            var box = new CheckBox { Tag = name, Content = name, Margin = new Thickness(0, 4, 0, 4) };
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            box.Checked += (_, _) => Problem.Visibility = Visibility.Collapsed;
            FolderList.Children.Add(box);
        }
        Loaded += (_, _) => LabelBox.Focus();
    }

    /// <summary>The name box.</summary>
    public string Label
    {
        get => LabelBox.Text;
        set => LabelBox.Text = value;
    }

    /// <summary>The folders ticked now.</summary>
    public IReadOnlyList<string> ChosenFolders => FolderList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

    public bool WholeNest
    {
        get => WholeChoice.IsChecked == true;
        set
        {
            WholeChoice.IsChecked = value;
            FoldersChoice.IsChecked = !value;
        }
    }

    public bool CanChange
    {
        get => EditChoice.IsChecked == true;
        set => EditChoice.IsChecked = value;
    }

    /// <summary>What the window says went wrong, or "".</summary>
    public string ProblemText => Problem.Visibility == Visibility.Visible ? Problem.Text : string.Empty;

    /// <summary>True once the nest made the link and the window shows its password.</summary>
    public bool ShowingResult => ResultPanel.Visibility == Visibility.Visible;

    public string ResultAddress => AddressBox.Text;

    public string ResultUsername => UsernameBox.Text;

    public string ResultPassword => PasswordBox.Text;

    public string CopyNoteText => CopyNote.Text;

    private void OnChanged(object sender, TextChangedEventArgs e) => Problem.Visibility = Visibility.Collapsed;

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (FolderBox is null)
            return; // while the window is being built
        FolderBox.Visibility = FoldersChoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Problem.Visibility = Visibility.Collapsed;
    }

    private async void OnMake(object sender, RoutedEventArgs e)
    {
        var whole = WholeNest;
        IReadOnlyList<string> folders = whole ? [] : ChosenFolders;
        if (WebDavChoices.Problem(LabelBox.Text, whole, folders) is { } wrong)
        {
            ShowProblem(wrong);
            return;
        }
        MakeButton.IsEnabled = false;
        MakeLabel.Text = "Asking the nest…";
        Problem.Visibility = Visibility.Collapsed;
        (NewWebDavLink? Link, string? Error) made;
        try
        {
            made = await _make(LabelBox.Text.Trim(), CanChange, whole ? null : folders);
        }
        catch (Exception)
        {
            made = (null, "Couldn't reach the nest. Try again in a moment.");
        }
        if (made.Link is null)
        {
            ShowProblem(made.Error ?? WebDavErrors.Sentence(null));
            MakeLabel.Text = "Make the link";
            MakeButton.IsEnabled = true;
            return;
        }
        ShowResult(made.Link);
    }

    private void ShowResult(NewWebDavLink link)
    {
        ResultHeading.Text = "The link \"" + link.Label + "\" is ready";
        AddressBox.Text = _address;
        UsernameBox.Text = link.Username ?? WebDavChoices.Slug(link.Label);
        PasswordBox.Text = link.Password;
        FormPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        Title = "Your new link";
    }

    private void ShowProblem(string problem)
    {
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
    }

    private void Copied(string text, string what) =>
        CopyNote.Text = _copy(text) ? what + " copied." : "Couldn't copy: another program is using the clipboard. Press Copy again.";

    private void OnCopyAddress(object sender, RoutedEventArgs e) => Copied(AddressBox.Text, "Address");

    private void OnCopyUsername(object sender, RoutedEventArgs e) => Copied(UsernameBox.Text, "Username");

    private void OnCopyPassword(object sender, RoutedEventArgs e) => Copied(PasswordBox.Text, "Password");

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnDone(object sender, RoutedEventArgs e)
    {
        PasswordBox.Text = string.Empty; // gone from the window the moment it is closed
        Close();
    }
}
