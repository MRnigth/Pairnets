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

    public SettingsWindow(ClientSettings current, ISecretProtector? protector, bool autoStart, Action<string>? openUrl = null, string? nestHint = null)
    {
        InitializeComponent();
        Title = "Pairnets – sign in";
        SignIn = new SignInView(current, protector, autoStart, openUrl ?? (_ => { }), nestHint);
        SignIn.SignedIn += (settings, key) =>
        {
            Result = settings;
            PlainToken = key;
            Close();
        };
        Host.Content = SignIn;
    }

    /// <summary>The sign-in steps.</summary>
    public SignInView SignIn { get; }

    /// <summary>The settings to save, when the window closes with a result.</summary>
    public ClientSettings? Result { get; private set; }

    public string? PlainToken { get; private set; }
}
