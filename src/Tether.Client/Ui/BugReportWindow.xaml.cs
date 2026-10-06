using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Tether.Client.Themes;
using Tether.Core.Client;
using Tether.Core.Settings;

namespace Tether.Client.Ui;

/// <summary>"Report a bug": builds the report, copies it to the clipboard and saves it next to the logs. Nothing is sent anywhere.</summary>
public partial class BugReportWindow : Window
{
    private readonly Func<Task<string>> _build;
    private readonly Action<string> _open;
    private string? _path;

    /// <param name="build">Builds the report text (see <see cref="BugReport.BuildAsync"/>).</param>
    /// <param name="open">Opens the saved file.</param>
    /// <param name="afterError">True when Tether opened this after an unexpected error.</param>
    public BugReportWindow(Func<Task<string>> build, Action<string> open, bool afterError = false)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        _build = build;
        _open = open;
        if (afterError)
            Heading.Text = "Tether hit an unexpected error and kept running. Building a bug report…";
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
            _path = BugReport.Save(report, TetherPaths.LogsDir);
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
            + (_path is null ? "" : $" and saved as {Path.GetFileName(_path)} in Tether's log folder")
            + ". Paste it to whoever helps you; nothing was sent anywhere.";
    }

    private static bool Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException)
        {
            return false; // another app holds the clipboard
        }
    }

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
