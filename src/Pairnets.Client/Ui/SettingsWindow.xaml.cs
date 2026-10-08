using System.Windows;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Client.Ui;

/// <summary>
/// Setting up this computer, in a dialog of its own: signing in with the browser (<see cref="SignInView"/>),
/// or, under "Advanced", the settings form with the server address and token (<see cref="SettingsView"/>).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ClientSettings _current;
    private readonly ISecretProtector _protector;
    private readonly UpdateService? _updates;
    private readonly string? _serverVersionText;

    public SettingsWindow(ClientSettings current, ISecretProtector protector, bool firstRun, UpdateService? updates = null,
        string? serverVersionText = null, Action<string>? openUrl = null, string? nestHint = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Icon = AppIcons.WindowIcon;
        _current = current;
        _protector = protector;
        _updates = updates;
        _serverVersionText = serverVersionText;
        Title = firstRun ? "Pairnets – set up this computer" : "Pairnets – settings";
        if (firstRun)
        {
            SignIn = new SignInView(current, protector, openUrl ?? (_ => { }), nestHint);
            SignIn.SignedIn += (settings, key) =>
            {
                Result = settings;
                PlainToken = key;
                DialogResult = true;
            };
            SignIn.AdvancedRequested += () => ShowForm(firstRun: true);
            Host.Content = SignIn;
        }
        else
        {
            ShowForm(firstRun: false);
        }
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                Close();
        };
    }

    /// <summary>The sign-in steps (first run only).</summary>
    public SignInView? SignIn { get; }

    /// <summary>The settings form, once shown.</summary>
    public SettingsView? View { get; private set; }

    /// <summary>The settings to save (token protected) when the dialog returns true.</summary>
    public ClientSettings? Result { get; private set; }

    /// <summary>The plain token or key for this session (never written to disk in plain text).</summary>
    public string? PlainToken { get; private set; }

    /// <summary>The address-and-token form ("Advanced").</summary>
    public void ShowForm(bool firstRun)
    {
        View = new SettingsView(_current, _protector, firstRun, _updates, _serverVersionText);
        View.Saved += v =>
        {
            Result = v.Result;
            PlainToken = v.PlainToken;
            DialogResult = true;
        };
        View.Cancelled += Close;
        Host.Content = View;
    }

    public void ShowTestResult(bool? ok, string text) => View?.ShowTestResult(ok, text);
}
