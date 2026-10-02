using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tether.Core;
using Tether.Core.Client;

namespace Tether.Desktop.Views;

/// <summary>"Your server should be updated": asks, runs the update and shows how it went.</summary>
public partial class ServerUpdateWindow : Window
{
    private readonly ClientSession? _session;

    public ServerUpdateWindow()
        : this(null, "1.0.52")
    {
    }

    public ServerUpdateWindow(ClientSession? session, string serverVersion, string? appVersion = null)
    {
        InitializeComponent();
        _session = session;
        ServerVersion = serverVersion;
        appVersion ??= TetherInfo.ProductVersion;
        Explanation.Text = $"This app is version {appVersion}, but your server runs {serverVersion}. Updating is recommended so both sides have the latest fixes.";
        VersionsText.Text = $"{serverVersion} → {appVersion}";
    }

    public string ServerVersion { get; }

    /// <summary>Set when the user chose "Don't ask for this version".</summary>
    public bool Skipped { get; private set; }

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        Skipped = true;
        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
            return;
        ShowBusy("Asking the server to update");
        var result = await _session.UpdateServerAsync(new Progress<string>(ShowBusy), CancellationToken.None);
        ShowResult(result);
    }

    internal void ShowBusy(string step)
    {
        AskPanel.IsVisible = false;
        ResultPanel.IsVisible = false;
        BusyPanel.IsVisible = true;
        Heading.Text = "Updating your server…";
        StepText.Text = step;
    }

    internal void ShowResult(ServerUpdateResult result)
    {
        AskPanel.IsVisible = false;
        BusyPanel.IsVisible = false;
        ResultPanel.IsVisible = true;
        Heading.Text = result.Success ? $"Server updated to {result.ServerVersion}" : "The server didn't update";
        Explanation.Text = result.Success ? "Syncing has resumed." : result.Message
            + (result.CanUpdateItself ? string.Empty : " Run this once on the server; after that it can update from the app:");
        Badge.Fill = Visuals.Resource<IBrush>(result.Success ? "S.Green" : "S.Orange");
        BadgeIcon.Data = Visuals.Resource<Geometry>(result.Success ? "I.Check" : "I.Warning");
        CommandPanel.IsVisible = !result.Success && !result.CanUpdateItself;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(CommandText.Text ?? string.Empty);
    }
}
