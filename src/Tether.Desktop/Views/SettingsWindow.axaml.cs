using Avalonia.Controls;
using Tether.Core.Client;
using Tether.Core.Settings;

namespace Tether.Desktop.Views;

/// <summary>First-time setup: the settings form (<see cref="SettingsView"/>) in a window of its own.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
        : this(new ClientSettings(), null, firstRun: true, autoStart: false)
    {
    }

    public SettingsWindow(ClientSettings current, ISecretProtector? protector, bool firstRun, bool autoStart, UpdateService? updates = null, string? serverVersionText = null)
    {
        InitializeComponent();
        Title = firstRun ? "Tether – first-time setup" : "Tether – settings";
        View = new SettingsView(current, protector, firstRun, autoStart, updates, serverVersionText);
        View.Saved += v =>
        {
            Result = v.Result;
            PlainToken = v.PlainToken;
            Close();
        };
        View.Cancelled += Close;
        Host.Content = View;
    }

    public SettingsView View { get; }

    /// <summary>The settings to save, when the window closes with a result.</summary>
    public ClientSettings? Result { get; private set; }

    public string? PlainToken { get; private set; }

    internal void ShowTestResult(bool? ok, string text) => View.ShowTestResult(ok, text);
}
