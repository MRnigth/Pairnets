using System.Windows;
using Pairnets.Client.Themes;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Download a link on the nest" (1.0.61.8): the link and the nest folder it goes in ("Downloads" unless changed), then
/// Start download hands it to the nest, which downloads it by itself. The window closes once the nest took it.
/// </summary>
public partial class NestDownloadWindow : Window
{
    private readonly Func<string, string, Task<string?>> _start;
    private readonly Func<string, string?> _pickFolder;

    /// <param name="start">Asks the nest to download the link into the folder; returns null when it took it, or what went wrong.</param>
    /// <param name="pickFolder">"Change…": the folder picked (from the current one), or null when nothing was picked.</param>
    public NestDownloadWindow(Func<string, string, Task<string?>> start, Func<string, string?> pickFolder)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _start = start;
        _pickFolder = pickFolder;
        Loaded += (_, _) => LinkBox.Focus();
    }

    /// <summary>The link box.</summary>
    public string Link
    {
        get => LinkBox.Text;
        set => LinkBox.Text = value;
    }

    /// <summary>The nest folder the file goes in ("" = the top of the nest).</summary>
    public string Folder { get; private set; } = NestDownloadRules.DefaultFolder;

    /// <summary>What the window says went wrong, or "".</summary>
    public string ProblemText => Problem.Visibility == Visibility.Visible ? Problem.Text : string.Empty;

    /// <summary>
    /// An encrypted nest: the window says before anything starts that the file won't be end-to-end encrypted while it
    /// waits on the nest (<see cref="NestDownloadsSection.SealedWarning"/>).
    /// </summary>
    public bool Sealed
    {
        get => SealedBox.Visibility == Visibility.Visible;
        set
        {
            SealedBox.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SealedText.Text = value ? NestDownloadsSection.SealedWarning : string.Empty;
        }
    }

    /// <summary>True once the nest took the link.</summary>
    public bool Started { get; private set; }

    private void OnLinkChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Problem.Visibility = Visibility.Collapsed;

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        if (_pickFolder(Folder) is not { } folder)
            return;
        Folder = folder;
        FolderText.Text = NestDownloadsSection.FolderName(folder);
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (NestDownloadRules.LinkProblem(LinkBox.Text) is { } wrong)
        {
            ShowProblem(wrong);
            return;
        }
        StartButton.IsEnabled = false;
        LinkBox.IsReadOnly = true;
        StartLabel.Text = "Asking the nest…";
        Problem.Visibility = Visibility.Collapsed;
        string? problem;
        try
        {
            problem = await _start(LinkBox.Text.Trim(), Folder);
        }
        catch (Exception)
        {
            problem = "Couldn't reach the nest. Try again in a moment.";
        }
        if (problem is null)
        {
            Started = true;
            Close();
            return;
        }
        ShowProblem(problem);
        StartLabel.Text = "Start download";
        StartButton.IsEnabled = true;
        LinkBox.IsReadOnly = false;
    }

    private void ShowProblem(string problem)
    {
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
