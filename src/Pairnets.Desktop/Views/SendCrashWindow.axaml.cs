using Avalonia.Controls;
using Avalonia.Interactivity;
using Pairnets.Core.Client;

namespace Pairnets.Desktop.Views;

/// <summary>
/// "Send crash report to Pairnets": shows exactly what will be sent (<see cref="CrashSender.Preview"/>), takes an
/// optional note, and sends only when the user presses Send.
/// </summary>
public partial class SendCrashWindow : Window
{
    private readonly Func<string?, Task<string?>> _send;

    public SendCrashWindow()
        : this([], _ => Task.FromResult<string?>(null))
    {
    }

    /// <param name="cards">The cards that will be sent (<see cref="CrashSender.Unsent"/>).</param>
    /// <param name="send">Sends them with the note; returns null when it worked, or what went wrong.</param>
    public SendCrashWindow(IReadOnlyList<CrashCard> cards, Func<string?, Task<string?>> send)
    {
        InitializeComponent();
        _send = send;
        PreviewText.Text = CrashSender.Preview(cards);
        if (cards.Count > 1)
            Heading.Text = $"Send these {cards.Count} crash reports to us?";
    }

    /// <summary>The user's note ("What were you doing?").</summary>
    public string Note
    {
        get => NoteBox.Text ?? string.Empty;
        set => NoteBox.Text = value;
    }

    /// <summary>True once the report went out.</summary>
    public bool Sent { get; private set; }

    // Exposed for the headless UI test.
    internal string PreviewValue => PreviewText.Text ?? string.Empty;
    internal string? ProblemValue => Problem.IsVisible ? Problem.Text : null;

    internal async Task SendAsync()
    {
        SendButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        NoteBox.IsReadOnly = true;
        SendLabel.Text = "Sending…";
        Problem.IsVisible = false;
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
        Problem.IsVisible = true;
        SendLabel.Text = "Try again";
        SendButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        NoteBox.IsReadOnly = false;
    }

    private async void OnSend(object? sender, RoutedEventArgs e) => await SendAsync();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
