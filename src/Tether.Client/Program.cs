using System.Windows;

namespace Tether.Client;

internal static class Program
{
    private const string MutexName = @"Local\Tether.Client.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Tether is already running. Look for its icon in the notification area.", "Tether",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        var app = new App();
        return app.Run();
    }
}
