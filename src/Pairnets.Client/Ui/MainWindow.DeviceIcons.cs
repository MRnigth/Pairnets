using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

public partial class MainWindow
{
    /// <summary>Listens for a pencil's pick anywhere on the Devices page (the event bubbles up from the card).</summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        DevicesPage.AddHandler(DeviceIconPencil.IconChosenEvent, new EventHandler<IconChosenEventArgs>(OnDeviceIconChosen));
    }

    /// <summary>A pencil's chooser picked an icon: save it on the nest (the Devices list updates when the nest answers).</summary>
    private async void OnDeviceIconChosen(object? sender, IconChosenEventArgs e)
    {
        if (_actions is null || e.DeviceId is not { Length: > 0 } id)
            return;
        e.Handled = true;
        try
        {
            await _actions.SetDeviceIcon(id, e.Wire);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The nest could not be reached; the old icon stays.
        }
    }
}
