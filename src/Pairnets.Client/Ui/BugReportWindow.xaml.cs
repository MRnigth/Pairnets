using System.IO;
using System.Windows;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Report a bug": builds the report, copies it to the clipboard and saves it next to the logs. Nothing is sent anywhere,
/// unless the user presses "Send crash report to Pairnets" (shown when there are crash cards not sent yet).
/// </summary>
public partial class BugReportWindow : Window
{
    private readonly Func<Task<string>> _build;
    private readonly Action<string> _open;
    private readonly string _logsDir;
    private readonly string? _crashesDir;
    private readonly Func<IReadOnlyList<CrashCard>, string?, Task<string?>>? _sendCrashes;
    private string? _path;

    /// <param name="build">Builds the report text (see <see cref="BugReport.BuildAsync"/>).</param>
    /// <param name="open">Opens the saved file.</param>
    /// <param name="afterError">True when Pairnets opened this after an unexpected error.</param>
    /// <param name="logsDir">Where the report is saved: the log folder (null is the usual one).</param>
    /// <param name="crashesDir">The crash cards (<see cref="ClientEnvironment.CrashesDir"/>); null hides "Send crash report".</param>
    /// <param name="sendCrashes">Sends cards with the user's note (<see cref="CrashSender.SendAsync"/>); null or what went wrong.</param>
    public BugReportWindow(Func<Task<string>> build, Action<string> open, bool afterError = false, string? logsDir = null,
        string? crashesDir = null, Func<IReadOnlyList<CrashCard>, string?, Task<string?>>? sendCrashes = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _build = build;
        _open = open;
        _logsDir = logsDir ?? PairnetsPaths.LogsDir;
        _crashesDir = crashesDir;
        _sendCrashes = sendCrashes;
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
        StateCircle.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "T.OkPill");
        StateIcon.SetResourceReference(LineIcon.StrokeProperty, "T.OkText");
        StateIcon.Data = Visuals.Resource<System.Windows.Media.Geometry>("I.Check");
        Heading.Text = copied ? "Bug report copied" : "Bug report ready";
        Explanation.Text = (copied ? "It is on the clipboard" : "Copy it with \"Copy again\"")
            + (_path is null ? "" : $" and saved as {Path.GetFileName(_path)} in the Pairnets log folder")
            + ". Paste it to whoever helps you; nothing was sent anywhere.";
        ShowSendCrash();
    }

    private IReadOnlyList<CrashCard> Unsent() => _crashesDir is null ? [] : CrashSender.Unsent(_crashesDir);

    private void ShowSendCrash()
    {
        var cards = _sendCrashes is null ? [] : Unsent();
        CrashBanner.Visibility = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (cards.Count == 0)
            return;
        var times = cards.Sum(c => c.Count);
        CrashBannerTitle.Text = $"Pairnets crashed {(times == 1 ? "once" : $"{times} times")}. Help us fix it?";
    }

    private void OnSendCrash(object sender, RoutedEventArgs e)
    {
        var cards = Unsent();
        if (_sendCrashes is null || cards.Count == 0)
            return;
        var send = _sendCrashes;
        var window = new SendCrashWindow(cards, note => send(cards, note)) { Owner = this };
        window.ShowDialog();
        if (!window.Sent)
            return;
        SentChipText.Text = cards.Count == 1 ? "Crash report sent to Pairnets" : $"{cards.Count} crash reports sent to Pairnets";
        CrashBannerTitle.Text = "Thank you. The crash report was sent.";
        CrashBannerText.Text = "Only the crash report went out. The bug report below was not sent.";
        SendCrashButton.Visibility = Visibility.Collapsed;
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
