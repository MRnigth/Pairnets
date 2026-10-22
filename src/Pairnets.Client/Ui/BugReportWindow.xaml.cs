using System.IO;
using System.Windows;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Report a bug": builds the report, copies it to the clipboard and saves it next to the logs. Nothing is sent anywhere,
/// unless the user presses "Send bug report" (the cleaned report, <see cref="BugReportSender"/>) or "Send crash report"
/// (shown when there are crash cards not sent yet), and then Send in the window that shows exactly what goes out.
/// </summary>
public partial class BugReportWindow : Window
{
    private readonly Func<Task<string>> _build;
    private readonly Action<string> _open;
    private readonly string _logsDir;
    private readonly string? _crashesDir;
    private readonly Func<IReadOnlyList<CrashCard>, string?, Task<string?>>? _sendCrashes;
    private readonly Func<string, string>? _prepareReport;
    private readonly Func<string, string?, Task<string?>>? _sendReport;
    private string? _path;
    private bool _reportSent;
    private int _crashesSent;

    /// <param name="build">Builds the report text (see <see cref="BugReport.BuildAsync"/>).</param>
    /// <param name="open">Opens the saved file.</param>
    /// <param name="afterError">True when Pairnets opened this after an unexpected error.</param>
    /// <param name="logsDir">Where the report is saved: the log folder (null is the usual one).</param>
    /// <param name="crashesDir">The crash cards (<see cref="ClientEnvironment.CrashesDir"/>); null hides "Send crash report".</param>
    /// <param name="sendCrashes">Sends cards with the user's note (<see cref="CrashSender.SendAsync"/>); null or what went wrong.</param>
    /// <param name="prepareReport">The report as it would be sent (<see cref="BugReportSender.Prepare"/>).</param>
    /// <param name="sendReport">Sends that with the user's note (<see cref="BugReportSender.SendAsync"/>); null or what went
    /// wrong. Null (or no <paramref name="prepareReport"/>) hides "Send bug report".</param>
    public BugReportWindow(Func<Task<string>> build, Action<string> open, bool afterError = false, string? logsDir = null,
        string? crashesDir = null, Func<IReadOnlyList<CrashCard>, string?, Task<string?>>? sendCrashes = null,
        Func<string, string>? prepareReport = null, Func<string, string?, Task<string?>>? sendReport = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _build = build;
        _open = open;
        _logsDir = logsDir ?? PairnetsPaths.LogsDir;
        _crashesDir = crashesDir;
        _sendCrashes = sendCrashes;
        _prepareReport = prepareReport;
        _sendReport = prepareReport is null ? null : sendReport;
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
            + ". Paste it to whoever helps you.";
        ShowSendCrash();
        ShowSendReport();
    }

    private void ShowSendReport()
    {
        ReportBanner.Visibility = _sendReport is null ? Visibility.Collapsed : Visibility.Visible;
        ReportBannerTitle.Text = SendReportText.BannerTitle;
        ReportBannerText.Text = SendReportText.BannerText;
    }

    /// <summary>The chip and banners after a send window: say what went out.</summary>
    private void ShowSent()
    {
        SentChipText.Text = SendReportText.Chip(_reportSent, _crashesSent);
        if (_crashesSent > 0)
            CrashBannerText.Text = SendReportText.CrashSent(_reportSent);
        if (!_reportSent)
            return;
        ReportBannerTitle.Text = SendReportText.SentTitle;
        ReportBannerText.Text = SendReportText.SentText;
        ReportBannerIcon.Data = Visuals.Resource<System.Windows.Media.Geometry>("I.Check");
        SendReportButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>The send window for the bug report, exactly as it would be sent (null when sending is off).</summary>
    public SendCrashWindow? ReportSendWindow()
    {
        if (_prepareReport is null || _sendReport is null)
            return null;
        var prepared = _prepareReport(ReportText.Text);
        var send = _sendReport;
        return SendCrashWindow.ForBugReport(prepared, note => send(prepared, note));
    }

    /// <summary>After the bug report's send window: when it sent, the chip and banner say so and the button goes.</summary>
    public void AfterReportSend(bool sent)
    {
        if (!sent)
            return;
        _reportSent = true;
        ShowSent();
    }

    private void OnSendReport(object sender, RoutedEventArgs e)
    {
        if (ReportSendWindow() is not { } window)
            return;
        window.Owner = this;
        Dialogs.ShowWindow(window);
        AfterReportSend(window.Sent);
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
        Dialogs.ShowWindow(window);
        if (!window.Sent)
            return;
        _crashesSent = cards.Count;
        CrashBannerTitle.Text = "Thank you. The crash report was sent.";
        SendCrashButton.Visibility = Visibility.Collapsed;
        ShowSent();
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
