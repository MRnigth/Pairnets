using Avalonia.Controls;
using Avalonia.Interactivity;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// "Make a link…" under Files in Finder or Explorer: a name, the whole nest or chosen top-level folders, "View only" (the
/// default) or "Can change"; then the nest makes the link and the window shows its address, username and password with
/// Copy buttons. The password is shown once and lives only in this window (never in a log, state.db or the settings). The
/// Windows app has the same window.
/// </summary>
public partial class WebDavLinkWindow : Window
{
    private readonly string _address = string.Empty;
    private readonly Func<string, bool, IReadOnlyList<string>?, Task<(NewWebDavLink? Link, string? Error)>> _make;
    private readonly Func<string, Task<bool>> _copy;

    public WebDavLinkWindow()
        : this("https://pairnets.app/nest.nst_00000000000000000000000000/", [], (_, _, _) => Task.FromResult<(NewWebDavLink?, string?)>((null, null)), _ => Task.FromResult(false))
    {
    }

    /// <param name="address">What to type in Finder or Explorer (<see cref="WebDavSection.Address"/>).</param>
    /// <param name="folders">The nest's top-level folders, for "Chosen folders".</param>
    /// <param name="make">Asks the nest to make the link (name, can change, folders or null for the whole nest).</param>
    /// <param name="copy">Puts text on the clipboard; false when it can't (another program holds it).</param>
    public WebDavLinkWindow(string address, IReadOnlyList<string> folders, Func<string, bool, IReadOnlyList<string>?, Task<(NewWebDavLink? Link, string? Error)>> make, Func<string, Task<bool>> copy)
    {
        InitializeComponent();
        _address = address;
        _make = make;
        _copy = copy;
        FolderNote.Text = folders.Count == 0 ? "There are no folders on the nest yet. Use the whole nest, or make a folder first." : "Everything inside a folder comes with it.";
        foreach (var name in folders)
        {
            var box = new CheckBox { Tag = name, Content = name, Margin = new Avalonia.Thickness(0, 4) };
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            box.IsCheckedChanged += (_, _) => Problem.IsVisible = false;
            FolderList.Children.Add(box);
        }
        Opened += (_, _) => LabelBox.Focus();
    }

    /// <summary>The name box.</summary>
    internal string Label
    {
        get => LabelBox.Text ?? string.Empty;
        set => LabelBox.Text = value;
    }

    /// <summary>The folders ticked now.</summary>
    internal IReadOnlyList<string> ChosenFolders => FolderList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();

    internal bool WholeNest
    {
        get => WholeChoice.IsChecked == true;
        set
        {
            WholeChoice.IsChecked = value;
            FoldersChoice.IsChecked = !value;
        }
    }

    internal bool CanChange
    {
        get => EditChoice.IsChecked == true;
        set => EditChoice.IsChecked = value;
    }

    /// <summary>What the window says went wrong, or "".</summary>
    internal string ProblemText => Problem.IsVisible ? Problem.Text ?? string.Empty : string.Empty;

    /// <summary>True once the nest made the link and the window shows its password.</summary>
    internal bool ShowingResult => ResultPanel.IsVisible;

    internal string ResultAddress => AddressBox.Text ?? string.Empty;

    internal string ResultUsername => UsernameBox.Text ?? string.Empty;

    internal string ResultPassword => PasswordBox.Text ?? string.Empty;

    internal string CopyNoteText => CopyNote.Text ?? string.Empty;

    private void OnChanged(object? sender, TextChangedEventArgs e) => Problem.IsVisible = false;

    private void OnScopeChanged(object? sender, RoutedEventArgs e)
    {
        if (FolderBox is null)
            return; // while the window is being built
        FolderBox.IsVisible = FoldersChoice.IsChecked == true;
        Problem.IsVisible = false;
    }

    internal async void OnMake(object? sender, RoutedEventArgs e)
    {
        var whole = WholeNest;
        IReadOnlyList<string> folders = whole ? [] : ChosenFolders;
        if (WebDavChoices.Problem(Label, whole, folders) is { } wrong)
        {
            ShowProblem(wrong);
            return;
        }
        MakeButton.IsEnabled = false;
        MakeLabel.Text = "Asking the nest…";
        Problem.IsVisible = false;
        (NewWebDavLink? Link, string? Error) made;
        try
        {
            made = await _make(Label.Trim(), CanChange, whole ? null : folders);
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
        FormPanel.IsVisible = false;
        ResultPanel.IsVisible = true;
        Title = "Your new link";
    }

    private void ShowProblem(string problem)
    {
        Problem.Text = problem;
        Problem.IsVisible = true;
    }

    private async Task CopiedAsync(string text, string what) =>
        CopyNote.Text = await _copy(text) ? what + " copied." : "Couldn't copy: another program is using the clipboard. Press Copy again.";

    internal Task CopyAddressAsync() => CopiedAsync(AddressBox.Text ?? string.Empty, "Address");

    internal Task CopyUsernameAsync() => CopiedAsync(UsernameBox.Text ?? string.Empty, "Username");

    internal Task CopyPasswordAsync() => CopiedAsync(PasswordBox.Text ?? string.Empty, "Password");

    private async void OnCopyAddress(object? sender, RoutedEventArgs e) => await CopyAddressAsync();

    private async void OnCopyUsername(object? sender, RoutedEventArgs e) => await CopyUsernameAsync();

    private async void OnCopyPassword(object? sender, RoutedEventArgs e) => await CopyPasswordAsync();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnDone(object? sender, RoutedEventArgs e)
    {
        PasswordBox.Text = string.Empty; // gone from the window the moment it is closed
        Close();
    }
}
