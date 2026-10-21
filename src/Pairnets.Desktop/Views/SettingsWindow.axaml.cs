using Avalonia.Controls;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Desktop.Views;

/// <summary>
/// Setting up this computer, in a window of its own: the sign-in (<see cref="SignInView"/>) and nothing
/// else. There is no form for a server address and token any more.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(new ClientSettings(), null, autoStart: false)
    {
    }

    /// <param name="adding">Set for "Add a nest": signing in to one more nest (see <see cref="NestAdding"/>).</param>
    public SettingsWindow(ClientSettings current, ISecretProtector? protector, bool autoStart, Action<string>? openUrl = null, string? nestHint = null,
        NestAdding? adding = null)
    {
        InitializeComponent();
        Title = adding is null ? "Pairnets – sign in" : "Pairnets – add a nest";
        SignIn = new SignInView(current, protector, autoStart, openUrl ?? (_ => { }), nestHint, adding);
        SignIn.SignedIn += (settings, key) =>
        {
            Result = settings;
            PlainToken = key;
            Close();
        };
        Host.Content = SignIn;
        KeyDown += async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape)
                return;
            e.Handled = true;
            // Escape must not throw away a sign-in that is under way (waiting in the browser, or an email typed in).
            if (!SignIn.InProgress || await Dialogs.ConfirmAsync(this, "Pairnets", "Stop signing in?"))
                Close();
        };
    }

    /// <summary>The sign-in steps.</summary>
    public SignInView SignIn { get; }

    /// <summary>The settings to save, when the window closes with a result.</summary>
    public ClientSettings? Result { get; private set; }

    public string? PlainToken { get; private set; }
}
