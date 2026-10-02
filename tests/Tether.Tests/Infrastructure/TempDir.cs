namespace Tether.Tests.Infrastructure;

/// <summary>A unique temporary directory deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir(string? prefix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tether-tests", (prefix ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(Path, recursive: true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }
}
