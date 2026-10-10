using System.IO;
using System.Windows;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Client.Ui;

/// <summary>"Report a bug": builds the report, copies it to the clipboard and saves it next to the logs. Nothing is sent anywhere.</summary>
public partial class BugReportWindow : Window
{
    private readonly Func<Task<string>> _build;
    private readonly Action<string> _open;
    private readonly string _logsDir;
    private string? _path;

    /// <param name="build">Builds the report text (see <see cref="BugReport.BuildAsync"/>).</param>
    /// <param name="open">Opens the saved file.</param>
    /// <param name="afterError">True when Pairnets opened this after an unexpected error.</param>
    /// <param name="logsDir">Where the report is saved: the log folder (null is the usual one).</param>
    public BugReportWindow(Func<Task<string>> build, Action<string> open, bool afterError = false, string? logsDir = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _build = build;
        _open = open;
        _logsDir = logsDir ?? PairnetsPaths.LogsDir;
        if (afterError)
            Heading.Text = "Pairnets hit an unexpected error and kept running. Building a bug report…";
        Loaded += async (_, _) => await BuildAsync();
    }

    private async Task BuildAsync()
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
            _path = BugReport.Save(report, _logsDir);
            OpenButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
        }
        var copied = Copy(report);
        CopyButton.IsEnabled = true;
        Heading.Text = copied ? "Bug report copied" : "Bug report ready";
        Explanation.Text = (copied ? "It is on the clipboard" : "Copy it with \"Copy again\"")
            + (_path is null ? "" : $" and saved as {Path.GetFileName(_path)} in the Pairnets log folder")
            + ". Paste it to whoever helps you; nothing was sent anywhere.";
    }

    private static bool Copy(string text) => Dialogs.CopyText(text); // false: another app holds the clipboard

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Copy(ReportText.Text))
            Heading.Text = "Bug report copied";
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (_path is not null)
            _open(_path);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
