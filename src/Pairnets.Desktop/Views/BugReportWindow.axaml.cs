using Avalonia.Controls;
using Avalonia.Interactivity;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Desktop.Views;

/// <summary>"Report a bug": builds the report, copies it to the clipboard and saves it next to the logs. Nothing is sent anywhere.</summary>
public partial class BugReportWindow : Window
{
    private readonly Func<Task<string>> _build;
    private readonly Action<string> _open;
    private string? _path;

    public BugReportWindow()
        : this(() => Task.FromResult("Pairnets bug report"), _ => { })
    {
    }

    /// <param name="build">Builds the report text (see <see cref="BugReport.BuildAsync"/>).</param>
    /// <param name="open">Opens the saved file.</param>
    /// <param name="afterError">True when Pairnets opened this after an unexpected error.</param>
    /// <param name="saveDirectory">Where the report is saved (default: Pairnets's log folder).</param>
    public BugReportWindow(Func<Task<string>> build, Action<string> open, bool afterError = false, string? saveDirectory = null)
    {
        InitializeComponent();
        _build = build;
        _open = open;
        SaveDirectory = saveDirectory ?? PairnetsPaths.LogsDir;
        if (afterError)
            Heading.Text = "Pairnets hit an unexpected error and kept running. Building a bug report…";
        Opened += async (_, _) => await BuildAsync();
    }

    internal string SaveDirectory { get; }

    // Exposed for the headless UI test.
    internal string ReportValue => ReportText.Text ?? string.Empty;
    internal string? SavedPath => _path;

    internal async Task BuildAsync()
    {
        string report;
        try
        {
            report = await _build();
        }
        catch (Exception ex)
        {
            report = "The bug report could not be completed:" + Environment.NewLine + ex;
        }
        ReportText.Text = report;
        try
        {
            _path = BugReport.Save(report, SaveDirectory);
            OpenButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
        var copied = await CopyAsync(report);
        CopyButton.IsEnabled = true;
        Heading.Text = copied ? "Bug report copied" : "Bug report ready";
        Explanation.Text = (copied ? "It is on the clipboard" : "Copy it with \"Copy again\"")
            + (_path is null ? "" : $" and saved as {Path.GetFileName(_path)} in Pairnets's log folder")
            + ". Paste it to whoever helps you; nothing was sent anywhere.";
    }

    private async Task<bool> CopyAsync(string text)
    {
        try
        {
            if (Clipboard is not { } clipboard)
                return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // no clipboard (headless, or another app holds it)
        }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (await CopyAsync(ReportText.Text ?? string.Empty))
            Heading.Text = "Bug report copied";
    }

    private void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (_path is not null)
            _open(_path);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
