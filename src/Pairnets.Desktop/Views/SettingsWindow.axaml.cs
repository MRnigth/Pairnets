using Avalonia.Controls;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;

namespace Pairnets.Desktop.Views;

/// <summary>
/// Setting up this computer, in a window of its own: signing in with the browser (<see cref="SignInView"/>),
/// or, under "Advanced", the settings form with the server address and token (<see cref="SettingsView"/>).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ClientSettings _current;
    private readonly ISecretProtector? _protector;
    private readonly bool _autoStart;
    private readonly UpdateService? _updates;
    private readonly string? _serverVersionText;

    public SettingsWindow()
        : this(new ClientSettings(), null, firstRun: true, autoStart: false)
    {
    }

    public SettingsWindow(ClientSettings current, ISecretProtector? protector, bool firstRun, bool autoStart, UpdateService? updates = null,
        string? serverVersionText = null, Action<string>? openUrl = null)
    {
        InitializeComponent();
        _current = current;
        _protector = protector;
        _autoStart = autoStart;
        _updates = updates;
        _serverVersionText = serverVersionText;
        Title = firstRun ? "Pairnets – set up this computer" : "Pairnets – settings";
        if (firstRun)
        {
            SignIn = new SignInView(current, protector, autoStart, openUrl ?? (_ => { }));
            SignIn.SignedIn += (settings, key) =>
            {
                Result = settings;
                PlainToken = key;
                Close();
            };
            SignIn.AdvancedRequested += () => ShowForm(firstRun: true);
            Host.Content = SignIn;
        }
        else
        {
            ShowForm(firstRun: false);
        }
    }

    /// <summary>The sign-in steps (first run only).</summary>
    public SignInView? SignIn { get; }

    /// <summary>The settings form, once shown.</summary>
    public SettingsView? View { get; private set; }

    /// <summary>The settings to save, when the window closes with a result.</summary>
    public ClientSettings? Result { get; private set; }

    public string? PlainToken { get; private set; }

    /// <summary>The address-and-token form ("Advanced").</summary>
    public void ShowForm(bool firstRun)
    {
        View = new SettingsView(_current, _protector, firstRun, _autoStart, _updates, _serverVersionText);
        View.Saved += v =>
        {
            Result = v.Result;
            PlainToken = v.PlainToken;
            Close();
        };
        View.Cancelled += Close;
        Host.Content = View;
    }

    internal void ShowTestResult(bool? ok, string text) => View?.ShowTestResult(ok, text);
}
