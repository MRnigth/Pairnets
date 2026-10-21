using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// Release files for the fake release feeds, laid out like the release workflow makes them: pairnets-client-win-x64.zip
/// holds Pairnets.exe and README-client.txt; pairnets-desktop-linux-x64.tar.gz holds a pairnets-desktop-linux-x64 folder
/// with Pairnets, install-desktop.sh and pairnets.png. TEST ONLY: the "app" inside is a few bytes of text.
/// </summary>
public static class TestUpdatePackages
{
    /// <summary>The bytes of <paramref name="asset"/>: an archive with <paramref name="app"/> as the app in it, or the bytes themselves.</summary>
    public static byte[] Make(string asset, byte[] app)
    {
        if (asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Zip(("Pairnets.exe", app), ("README-client.txt", Encoding.UTF8.GetBytes("Pairnets (test)\n")));
        if (asset.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            var folder = asset[..^".tar.gz".Length];
            return TarGz((folder + "/Pairnets", app), (folder + "/install-desktop.sh", Encoding.UTF8.GetBytes("#!/bin/sh\n")),
                (folder + "/pairnets.png", [0x89, 0x50, 0x4E, 0x47]));
        }
        return app;
    }

    public static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    public static byte[] TarGz(params (string Name, byte[] Bytes)[] files)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(bytes),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                });
            }
        }
        return ms.ToArray();
    }
}
