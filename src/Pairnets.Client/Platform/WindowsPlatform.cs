using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Platform;

/// <summary>The real Windows: DPAPI, the Recycle Bin, the per-user Run key and Explorer.</summary>
public sealed class WindowsPlatform : IWindowsPlatform
{
    public ISecretProtector Secrets { get; } = new DpapiProtector();

    public ILocalTrash CreateTrash(ILogger log) => new RecycleBinTrash(log);

    public bool IsAutoStartEnabled() => AutoStart.IsEnabled();

    public void SetAutoStart(bool enabled) => AutoStart.Set(enabled);

    public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public void Reveal(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();

    public void RunInstaller(string path, string arguments) =>
        Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true })?.Dispose();
}
