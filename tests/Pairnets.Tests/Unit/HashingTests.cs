using System.Text;
using Pairnets.Core.Hashing;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public class HashingTests
{
    [Fact]
    public void KnownAnswer()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentHash.Of("abc"u8)); // check-secrets: allow
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ContentHash.Of([])); // check-secrets: allow
    }

    [Fact]
    public async Task FileHashMatchesBytes()
    {
        using var dir = new TempDir();
        var data = new byte[3 * 1024 * 1024 + 17];
        new Random(1).NextBytes(data);
        var path = dir.Combine("f.bin");
        await File.WriteAllBytesAsync(path, data);
        var (hash, length) = await ContentHash.OfFileAsync(path);
        Assert.Equal(ContentHash.Of(data), hash);
        Assert.Equal(data.Length, length);
    }

    [Fact]
    public async Task HashingStreamHashesReadsAndWrites()
    {
        var data = Encoding.UTF8.GetBytes(new string('x', 100_000));
        await using (var reader = new HashingStream(new MemoryStream(data)))
        {
            await reader.CopyToAsync(Stream.Null);
            Assert.Equal(ContentHash.Of(data), reader.GetHash());
            Assert.Equal(data.Length, reader.BytesTransferred);
        }

        var sink = new MemoryStream();
        long lastProgress = 0;
        await using (var writer = new HashingStream(sink, leaveOpen: true, onProgress: p => lastProgress = p))
        {
            await writer.WriteAsync(data.AsMemory(0, 10));
            writer.Write(data, 10, data.Length - 10);
            Assert.Equal(ContentHash.Of(data), writer.GetHash());
        }
        Assert.Equal(data, sink.ToArray());
        Assert.Equal(data.Length, lastProgress);
    }

    [Fact]
    public void ValidatesHashShape()
    {
        Assert.True(ContentHash.IsValid(ContentHash.Of("x"u8)));
        Assert.False(ContentHash.IsValid(null));
        Assert.False(ContentHash.IsValid("ABC"));
        Assert.False(ContentHash.IsValid(ContentHash.Of("x"u8).ToUpperInvariant()));
        Assert.Equal(8, ContentHash.Short(ContentHash.Of("x"u8)).Length);
    }
}
