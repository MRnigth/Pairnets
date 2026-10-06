using System.Windows;
using Tether.Client.Themes;
using Tether.Core.Client;
using Tether.Core.Settings;

namespace Tether.Client.Ui;

/// <summary>First-time setup: the settings form (<see cref="SettingsView"/>) in a dialog of its own.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(ClientSettings current, ISecretProtector protector, bool firstRun, UpdateService? updates = null, string? serverVersionText = null)
    {
        ThemeManager.Attach(this);
        InitializeComponent();
        Title = firstRun ? "Tether – first-time setup" : "Tether – settings";
        View = new SettingsView(current, protector, firstRun, updates, serverVersionText);
        View.Saved += v =>
        {
            Result = v.Result;
            PlainToken = v.PlainToken;
            DialogResult = true;
        };
        View.Cancelled += Close;
        Host.Content = View;
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                Close();
        };
    }

    public SettingsView View { get; }

    /// <summary>The settings to save (token protected) when the dialog returns true.</summary>
    public ClientSettings? Result { get; private set; }

    /// <summary>The plain token for this session (never written to disk in plain text).</summary>
    public string? PlainToken { get; private set; }

    public void ShowTestResult(bool? ok, string text) => View.ShowTestResult(ok, text);
}
