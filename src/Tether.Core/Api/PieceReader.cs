using System.Security.Cryptography;
using Tether.Core.Hashing;

namespace Tether.Core.Api;

/// <summary>
/// Reads one piece at a time of a file that is uploaded in pieces, and hashes the whole file on the
/// way. A piece sent again after a broken connection is read again but never hashed twice: every
/// byte goes into the hash exactly once and in order, so the final hash is the file's, retries or not.
/// </summary>
internal sealed class PieceReader : Stream
{
    private readonly Stream _file;
    private readonly long _start;
    private readonly Action<long>? _onProgress;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _position;
    private long _end;

    /// <param name="file">A seekable stream; the upload covers it from its current position to the end.</param>
    public PieceReader(Stream file, Action<long>? onProgress = null)
    {
        _file = file;
        _start = file.Position;
        Size = file.Length - _start;
        _onProgress = onProgress;
    }

    /// <summary>Bytes to upload.</summary>
    public long Size { get; }

    /// <summary>How many bytes from the start have gone into the hash.</summary>
    public long HashedUpTo { get; private set; }

    /// <summary>Positions the reader at <paramref name="offset"/>; it then reads <paramref name="length"/> bytes.</summary>
    public void StartPiece(long offset, long length)
    {
        if (offset < 0 || offset > HashedUpTo || length < 0 || offset + length > Size)
            throw new TetherProtocolException($"Cannot continue the upload at byte {offset} (read so far: {HashedUpTo} of {Size}).");
        _file.Position = _start + offset;
        _position = offset;
        _end = offset + length;
    }

    /// <summary>The hash of the whole file, once every byte has been read.</summary>
    public string GetHash()
    {
        if (HashedUpTo != Size)
            throw new InvalidOperationException($"Only {HashedUpTo} of {Size} bytes were read.");
        return ContentHash.ToHex(_hash.GetCurrentHash());
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var want = (int)Math.Min(buffer.Length, _end - _position);
        if (want == 0)
            return 0;
        var n = await _file.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
        Advance(buffer.Span[..n]);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        var want = (int)Math.Min(buffer.Length, _end - _position);
        if (want == 0)
            return 0;
        var n = _file.Read(buffer[..want]);
        Advance(buffer[..n]);
        return n;
    }

    private void Advance(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            throw new IOException("The file got shorter while it was being uploaded.");
        var end = _position + data.Length;
        if (end > HashedUpTo)
        {
            _hash.AppendData(data[(int)(HashedUpTo - _position)..]);
            HashedUpTo = end;
        }
        _position = end;
        _onProgress?.Invoke(_position);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _end;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }
}
