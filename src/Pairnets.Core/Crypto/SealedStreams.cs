using System.Buffers;
using System.Security.Cryptography;
using Pairnets.Core.Hashing;

namespace Pairnets.Core.Crypto;

/// <summary>
/// The encrypted form of a plain file as a seekable stream, for an upload to an encrypted nest. The upload code moves it
/// back and forth like a file (pieces, a piece sent again, an upload an earlier attempt started, a request sent again
/// after "too many requests"), and every position gives exactly the bytes a fresh start would: the same header and data
/// key, so a segment is never encrypted twice with different content under the same nonce unless the file itself
/// changed. The plain bytes are hashed in order as they are first read; when the end is reached their SHA-256 must be
/// <c>expectedPlainSha256</c> (what the meta says), otherwise it throws <see cref="IOException"/> before the last
/// segment is made, so the nest never gets a whole version that doesn't match its meta.
/// </summary>
public sealed class SealedUploadStream : Stream
{
    private readonly VerifyingPlainStream _plain;
    private readonly long _plainLength;
    private readonly SealedHeader _header;
    private readonly byte[] _dataKey;
    private SealedEncryptStream _inner;

    /// <param name="plain">A seekable stream; the upload covers it from its current position for <paramref name="plainLength"/> bytes.</param>
    public SealedUploadStream(Stream plain, long plainLength, SealedHeader header, ReadOnlySpan<byte> dataKey, string expectedPlainSha256)
    {
        if (!plain.CanSeek)
            throw new ArgumentException("An encrypted upload needs a seekable file.", nameof(plain));
        _plain = new VerifyingPlainStream(plain, plain.Position, plainLength, expectedPlainSha256);
        _plainLength = plainLength;
        _header = header;
        _dataKey = dataKey.ToArray();
        Length = SealedFile.EncryptedLength(plainLength);
        _inner = Open(0);
    }

    public override long Length { get; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Position
    {
        get => _inner.Position;
        set
        {
            if (value == _inner.Position)
                return;
            if (value < 0 || value > Length)
                throw new ArgumentOutOfRangeException(nameof(value));
            _inner.Dispose();
            _inner = Open(value);
        }
    }

    private SealedEncryptStream Open(long offset)
    {
        var index = offset <= SealedHeader.Size ? 0 : (offset - SealedHeader.Size) / SealedSegments.EncryptedSegmentSize;
        _plain.Position = Math.Min(index * SealedSegments.SegmentSize, _plainLength);
        return new SealedEncryptStream(_plain, _plainLength, _header, _dataKey, offset, leaveOpen: true);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => Length + offset,
        };
        return Position;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _plain.Dispose();
            CryptographicOperations.ZeroMemory(_dataKey);
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// The plain file as the encryption reads it (positions relative to where the upload starts), hashing every byte the
    /// first time it is read, in order. Going back to the start begins a new hash (a whole new send); going back elsewhere
    /// reads bytes that were hashed already. The caller's stream is never closed here.
    /// </summary>
    private sealed class VerifyingPlainStream(Stream inner, long start, long length, string expected) : Stream
    {
        private IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;
        private long _hashedUpTo;
        private bool _verified;

        public override long Position
        {
            get => _position;
            set
            {
                inner.Position = start + value;
                _position = value;
                if (value == 0)
                {
                    _hash.Dispose();
                    _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    _hashedUpTo = 0;
                    _verified = false;
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin != SeekOrigin.Begin)
                throw new NotSupportedException();
            Position = offset;
            return offset;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            if (n > 0)
            {
                var end = _position + n;
                if (_position > _hashedUpTo)
                    throw new IOException("The file changed while it was being sent.");
                if (end > _hashedUpTo)
                {
                    _hash.AppendData(buffer[(int)(_hashedUpTo - _position)..n]);
                    _hashedUpTo = end;
                }
                _position = end;
            }
            if (!_verified && _hashedUpTo >= length)
            {
                // Every byte the meta speaks of was read: it must be the content the meta's hash says.
                if (_hashedUpTo > length || !string.Equals(ContentHash.ToHex(_hash.GetCurrentHash()), expected, StringComparison.Ordinal))
                    throw new IOException("The file changed while it was being sent.");
                _verified = true;
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Decrypts a <c>PNE1</c> file while it is written into this stream (a download) and writes the plain bytes on to the
/// destination, one segment at a time and only once that segment is proven genuine. The last segment is decrypted only in
/// <see cref="CompleteAsync"/>, when it is known to be the last, so a file the nest cut short or changed never gets out
/// whole. <see cref="HeaderReady"/> runs as soon as the header is in, before any plain byte is written, so the caller can
/// check it against the file's meta first. Throws <see cref="CryptographicException"/> on anything that isn't genuine.
/// </summary>
public sealed class SealedDecryptStream(Stream plain, KeyRing ring) : Stream
{
    private readonly byte[] _headerBytes = new byte[SealedHeader.Size];
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _segment;
    private byte[]? _output;
    private int _headerFill;
    private int _segmentFill;
    private long _index;
    private SealedSegments? _segments;

    /// <summary>Called once with the file's header, before its data key is used or any plain byte is written.</summary>
    public Action<SealedHeader>? HeaderReady { get; init; }

    public SealedHeader? Header { get; private set; }

    /// <summary>Plain bytes written to the destination so far.</summary>
    public long PlainWritten { get; private set; }

    /// <summary>The SHA-256 of all plain bytes, once <see cref="CompleteAsync"/> succeeded.</summary>
    public string? PlainSha256 { get; private set; }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        while (data.Length > 0)
        {
            if (_segments is null)
            {
                var take = Math.Min(SealedHeader.Size - _headerFill, data.Length);
                data.Span[..take].CopyTo(_headerBytes.AsSpan(_headerFill));
                _headerFill += take;
                data = data[take..];
                if (_headerFill == SealedHeader.Size)
                    OpenHeader();
                continue;
            }
            if (_segmentFill == SealedSegments.EncryptedSegmentSize)
                await DecryptSegmentAsync(last: false, cancellationToken).ConfigureAwait(false); // more follows: not the last
            var room = Math.Min(SealedSegments.EncryptedSegmentSize - _segmentFill, data.Length);
            data.Span[..room].CopyTo(_segment.AsSpan(_segmentFill));
            _segmentFill += room;
            data = data[room..];
        }
    }

    /// <summary>The whole file has arrived: decrypts the last segment. Throws if the file was cut short or changed.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_segments is null || _segmentFill < SealedSegments.TagSize)
            throw new CryptographicException("An encrypted file was cut short.");
        await DecryptSegmentAsync(last: true, ct).ConfigureAwait(false);
        PlainSha256 = ContentHash.ToHex(_hash.GetCurrentHash());
    }

    private void OpenHeader()
    {
        var header = SealedHeader.Parse(_headerBytes);
        Header = header;
        HeaderReady?.Invoke(header);
        var dataKey = header.UnwrapDataKey(ring);
        try
        {
            _segments = new SealedSegments(header, dataKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
        _segment = ArrayPool<byte>.Shared.Rent(SealedSegments.EncryptedSegmentSize);
        _output = ArrayPool<byte>.Shared.Rent(SealedSegments.SegmentSize);
    }

    private async Task DecryptSegmentAsync(bool last, CancellationToken ct)
    {
        var plainLength = _segmentFill - SealedSegments.TagSize;
        _segments!.Decrypt(_index, last, _segment.AsSpan(0, _segmentFill), _output);
        await plain.WriteAsync(_output.AsMemory(0, plainLength), ct).ConfigureAwait(false);
        _hash.AppendData(_output!, 0, plainLength);
        PlainWritten += plainLength;
        _index++;
        _segmentFill = 0;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Nothing to flush: a segment is written on only once it is proven genuine.</summary>
    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _segments?.Dispose();
            _hash.Dispose();
            if (_segment is not null)
                ArrayPool<byte>.Shared.Return(_segment, clearArray: true);
            if (_output is not null)
                ArrayPool<byte>.Shared.Return(_output, clearArray: true);
            _segment = _output = null;
        }
        base.Dispose(disposing);
    }
}
