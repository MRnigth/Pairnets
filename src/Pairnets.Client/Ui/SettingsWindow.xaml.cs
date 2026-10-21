using System.Windows;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Client.Ui;

/// <summary>
/// Setting up this computer, in a dialog of its own: the sign-in (<see cref="SignInView"/>) and nothing
/// else. There is no form for a server address and token any more.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <param name="adding">Set for "Add a nest": signing in to one more nest (see <see cref="NestAdding"/>).</param>
    public SettingsWindow(ClientSettings current, ISecretProtector protector, Action<string>? openUrl = null, string? nestHint = null, NestAdding? adding = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        Title = adding is null ? "Pairnets – sign in" : "Pairnets – add a nest";
        SignIn = new SignInView(current, protector, openUrl ?? (_ => { }), nestHint, adding);
        SignIn.SignedIn += (settings, key) =>
        {
            Result = settings;
            PlainToken = key;
            DialogResult = true;
        };
        Host.Content = SignIn;
        // Sized to the content once (460×660), then free to resize: the steps follow the window.
        ContentRendered += (_, _) =>
        {
            SizeToContent = SizeToContent.Manual;
            Host.Width = double.NaN;
            Host.Height = double.NaN;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                EscapePressed();
        };
    }

    /// <summary>
    /// Escape closes the window, but must not throw away a sign-in that is under way (waiting in the browser, or an
    /// email typed in): then it asks first.
    /// </summary>
    internal void EscapePressed()
    {
        if (SignIn.InProgress && Dialogs.Ask(this, "Stop signing in?", "Pairnets", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
            return;
        Close();
    }

    /// <summary>The sign-in steps.</summary>
    public SignInView SignIn { get; }

    /// <summary>The settings to save (key protected) when the dialog returns true.</summary>
    public ClientSettings? Result { get; private set; }

    /// <summary>The plain key for this session (never written to disk in plain text).</summary>
    public string? PlainToken { get; private set; }
}
