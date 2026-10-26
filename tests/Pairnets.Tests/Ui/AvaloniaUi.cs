namespace Pairnets.Tests.Ui;

/// <summary>
/// The window tests take turns, one class at a time (the other tests still run beside them). They share one UI
/// thread, and several close every window opened while they ran: side by side they closed each other's windows, and on
/// a slow runner one could wait on the UI thread for minutes.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AvaloniaUi
{
    public const string Name = "Avalonia UI";
}
