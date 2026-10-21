using System.Windows;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Send crash report to Pairnets": shows exactly what will be sent (<see cref="CrashSender.Preview"/>), takes an
/// optional note, and sends only when the user presses Send.
/// </summary>
public partial class SendCrashWindow : Window
{
    private readonly Func<string?, Task<string?>> _send;

    /// <param name="cards">The cards that will be sent (<see cref="CrashSender.Unsent"/>).</param>
    /// <param name="send">Sends them with the note; returns null when it worked, or what went wrong.</param>
    public SendCrashWindow(IReadOnlyList<CrashCard> cards, Func<string?, Task<string?>> send)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _send = send;
        PreviewText.Text = CrashSender.Preview(cards);
        if (cards.Count > 1)
            Heading.Text = $"Send these {cards.Count} crash reports to us?";
    }

    /// <summary>The user's note ("What were you doing?").</summary>
    public string Note
    {
        get => NoteBox.Text;
        set => NoteBox.Text = value;
    }

    /// <summary>True once the report went out.</summary>
    public bool Sent { get; private set; }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        NoteBox.IsReadOnly = true;
        SendLabel.Text = "Sending…";
        Problem.Visibility = Visibility.Collapsed;
        string? problem;
        try
        {
            problem = await _send(NoteBox.Text);
        }
        catch (Exception ex)
        {
            problem = "Couldn't send: " + ex.Message;
        }
        if (problem is null)
        {
            Sent = true;
            Close();
            return;
        }
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
        SendLabel.Text = "Try again";
        SendButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        NoteBox.IsReadOnly = false;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
