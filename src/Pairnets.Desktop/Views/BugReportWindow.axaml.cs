using Avalonia;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Desktop.Views;

/// <summary>
/// "Report a bug": builds the report, copies it to the clipboard and saves it next to the logs. Nothing is sent anywhere,
/// unless the user presses "Send crash report" (shown when there are crash cards not sent yet).
/// </summary>
public partial class BugReportWindow : Window
{
    private readonly Func<Task<string>> _build;
    private readonly Action<string> _open;
    private readonly string? _crashesDir;
    private readonly Func<IReadOnlyList<CrashCard>, string?, Task<string?>>? _sendCrashes;
    private string? _path;

    public BugReportWindow()
        : this(() => Task.FromResult("Pairnets bug report"), _ => { })
    {
    }

    /// <param name="build">Builds the report text (see <see cref="BugReport.BuildAsync"/>).</param>
    /// <param name="open">Opens the saved file.</param>
    /// <param name="afterError">True when Pairnets opened this after an unexpected error.</param>
    /// <param name="saveDirectory">Where the report is saved (default: Pairnets's log folder).</param>
    /// <param name="crashesDir">The crash cards (<see cref="ClientEnvironment.CrashesDir"/>); null hides "Send crash report".</param>
    /// <param name="sendCrashes">Sends cards with the user's note (<see cref="CrashSender.SendAsync"/>); null or what went wrong.</param>
    public BugReportWindow(Func<Task<string>> build, Action<string> open, bool afterError = false, string? saveDirectory = null,
        string? crashesDir = null, Func<IReadOnlyList<CrashCard>, string?, Task<string?>>? sendCrashes = null)
    {
        InitializeComponent();
        _build = build;
        _open = open;
        _crashesDir = crashesDir;
        _sendCrashes = sendCrashes;
        SaveDirectory = saveDirectory ?? PairnetsPaths.LogsDir;
        if (afterError)
            Heading.Text = "Pairnets hit an unexpected error and kept running. Building a bug report…";
        Opened += async (_, _) => await BuildAsync();
    }

    internal string SaveDirectory { get; }

    // Exposed for the headless UI test.
    internal string ReportValue => ReportText.Text ?? string.Empty;
    internal string? SavedPath => _path;
    internal bool CrashBannerShown => CrashBanner.IsVisible;
    internal string SentChipValue => SentChipText.Text ?? string.Empty;

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
        StatusCircle.Bind(Border.BackgroundProperty, StatusCircle.GetResourceObservable("T.OkPill"));
        StatusIcon.Bind(LineIcon.StrokeProperty, StatusIcon.GetResourceObservable("T.OkText"));
        StatusIcon.Data = Visuals.Resource<Avalonia.Media.Geometry>("I.Check");
        Explanation.Text = null;
        Explanation.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        Explanation.Inlines.Clear();
        Explanation.Inlines.Add(new Avalonia.Controls.Documents.Run((copied ? "It’s on your clipboard" : "Copy it with “Copy again”") + (_path is null ? "" : " and saved as ")));
        if (_path is not null)
        {
            var name = new Avalonia.Controls.Documents.Run(Path.GetFileName(_path)) { FontFamily = new Avalonia.Media.FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontSize = 12 };
            name.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, this.GetResourceObservable("T.Text"));
            Explanation.Inlines.Add(name);
        }
        Explanation.Inlines.Add(new Avalonia.Controls.Documents.Run(". Paste it to whoever helps you."));
        ShowSendCrash();
    }

    private IReadOnlyList<CrashCard> Unsent() => _crashesDir is null ? [] : CrashSender.Unsent(_crashesDir);

    private void ShowSendCrash()
    {
        var cards = _sendCrashes is null ? [] : Unsent();
        CrashBanner.IsVisible = cards.Count > 0;
        if (cards.Count == 0)
            return;
        var times = cards.Sum(c => c.Count);
        CrashBannerTitle.Text = $"Pairnets crashed {(times == 1 ? "once" : $"{times} times")}. Help us fix it?";
    }

    /// <summary>After the send window: when it sent, the chip and banner say so and the button goes.</summary>
    internal void AfterSend(bool sent, int count)
    {
        if (!sent)
            return;
        SentChipText.Text = count == 1 ? "Crash report sent to Pairnets" : $"{count} crash reports sent to Pairnets";
        CrashBannerTitle.Text = "Thank you. The crash report was sent.";
        CrashBannerText.Text = "Only the crash report went out. The bug report below was not sent.";
        SendCrashButton.IsVisible = false;
    }

    private async void OnSendCrash(object? sender, RoutedEventArgs e)
    {
        var cards = Unsent();
        if (_sendCrashes is null || cards.Count == 0)
            return;
        var send = _sendCrashes;
        var window = new SendCrashWindow(cards, note => send(cards, note));
        await window.ShowDialog(this);
        AfterSend(window.Sent, cards.Count);
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
