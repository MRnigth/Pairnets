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
    public SettingsWindow(ClientSettings current, ISecretProtector protector, Action<string>? openUrl = null, string? nestHint = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        Title = "Pairnets – sign in";
        SignIn = new SignInView(current, protector, openUrl ?? (_ => { }), nestHint);
        SignIn.SignedIn += (settings, key) =>
        {
            Result = settings;
            PlainToken = key;
            DialogResult = true;
        };
        Host.Content = SignIn;
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                Close();
        };
    }

    /// <summary>The sign-in steps.</summary>
    public SignInView SignIn { get; }

    /// <summary>The settings to save (key protected) when the dialog returns true.</summary>
    public ClientSettings? Result { get; private set; }

    /// <summary>The plain key for this session (never written to disk in plain text).</summary>
    public string? PlainToken { get; private set; }
}
