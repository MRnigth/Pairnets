using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Pairnets.Core.Hashing;

namespace Pairnets.Core.Crypto;

/// <summary>
/// The header of an encrypted file (format <c>PNE1</c>, docs/ENCRYPTION.md §2.2), <see cref="Size"/> bytes:
/// magic "PNE1", version 1, the ring generation (4 bytes, big-endian), a random 16-byte file id, a random 4-byte nonce
/// prefix, and the file's own random 32-byte data key wrapped with the generation's <c>content-wrap</c> key
/// (AES-256-GCM: 12-byte nonce, 32 bytes, 16-byte tag; the first 29 header bytes are its associated data).
/// Every upload gets a new header, so two versions of a file never share keys. The app keeps the header with an
/// unfinished upload so a resumed piece is byte-identical.
/// </summary>
public sealed class SealedHeader
{
    public const int Size = 89;
    private const int BoundLength = 29;
    private static ReadOnlySpan<byte> Magic => "PNE1"u8;
    private const byte Version = 1;

    private readonly byte[] _bytes;

    private SealedHeader(byte[] bytes)
    {
        _bytes = bytes;
        Generation = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(5, 4));
    }

    public int Generation { get; }

    /// <summary>The random per-file id the segments are bound to (not the path id, so a move needs no re-upload).</summary>
    public ReadOnlySpan<byte> FileId => _bytes.AsSpan(9, 16);

    internal ReadOnlySpan<byte> NoncePrefix => _bytes.AsSpan(25, 4);

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    /// <summary>SHA-256 of the header bytes; the encrypted meta pins it, so the nest can't swap two files' blobs.</summary>
    public string Hash => ContentHash.Of(_bytes);

    /// <summary>A new header for an upload under the ring's current generation, and its plain data key.</summary>
    public static (SealedHeader Header, byte[] DataKey) Create(KeyRing ring)
    {
        var generation = ring.Current;
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var bytes = new byte[Size];
        Magic.CopyTo(bytes);
        bytes[4] = Version;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(5, 4), generation);
        RandomNumberGenerator.Fill(bytes.AsSpan(9, 16 + 4));
        var nonce = bytes.AsSpan(BoundLength, 12);
        RandomNumberGenerator.Fill(nonce);
        using var wrap = new AesGcm(ring.ContentWrapKey(generation), 16);
        wrap.Encrypt(nonce, dataKey, bytes.AsSpan(BoundLength + 12, 32), bytes.AsSpan(BoundLength + 44, 16),
            bytes.AsSpan(0, BoundLength));
        return (new SealedHeader(bytes), dataKey);
    }

    public static SealedHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size || !bytes[..4].SequenceEqual(Magic))
            throw new CryptographicException("This isn't an encrypted Pairnets file.");
        if (bytes[4] != Version)
            throw new CryptographicException("This file was encrypted by a newer Pairnets version.");
        return new SealedHeader(bytes[..Size].ToArray());
    }

    /// <summary>The file's data key; throws <see cref="CryptographicException"/> if the header was changed.</summary>
    public byte[] UnwrapDataKey(KeyRing ring)
    {
        var dataKey = new byte[32];
        using var wrap = new AesGcm(ring.ContentWrapKey(Generation), 16);
        wrap.Decrypt(_bytes.AsSpan(BoundLength, 12), _bytes.AsSpan(BoundLength + 12, 32),
            _bytes.AsSpan(BoundLength + 44, 16), dataKey, _bytes.AsSpan(0, BoundLength));
        return dataKey;
    }
}

/// <summary>
/// The body of a <c>PNE1</c> file: 1 MiB plain segments, each AES-256-GCM with nonce = prefix ‖ 8-byte counter and
/// associated data = file id ‖ generation ‖ segment number ‖ last-segment flag, so the nest can't reorder, cut, swap
/// or graft segments without the app noticing. An empty file is one empty last segment.
/// </summary>
public sealed class SealedSegments : IDisposable
{
    public const int SegmentSize = 1024 * 1024;
    public const int TagSize = 16;
    public const int EncryptedSegmentSize = SegmentSize + TagSize;

    private readonly AesGcm _aes;
    private readonly byte[] _fileId;
    private readonly int _generation;
    private readonly byte[] _prefix;

    public SealedSegments(SealedHeader header, ReadOnlySpan<byte> dataKey)
    {
        _aes = new AesGcm(dataKey, TagSize);
        _fileId = header.FileId.ToArray();
        _generation = header.Generation;
        _prefix = header.NoncePrefix.ToArray();
    }

    public void Encrypt(long index, bool last, ReadOnlySpan<byte> plain, Span<byte> output)
    {
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[29];
        Bind(index, last, nonce, aad);
        _aes.Encrypt(nonce, plain, output[..plain.Length], output.Slice(plain.Length, TagSize), aad);
    }

    /// <summary>Decrypts one segment; throws <see cref="CryptographicException"/> if anything about it was changed.</summary>
    public void Decrypt(long index, bool last, ReadOnlySpan<byte> encrypted, Span<byte> plain)
    {
        if (encrypted.Length < TagSize)
            throw new CryptographicException("An encrypted file was cut short.");
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[29];
        Bind(index, last, nonce, aad);
        var bodyLength = encrypted.Length - TagSize;
        _aes.Decrypt(nonce, encrypted[..bodyLength], encrypted[bodyLength..], plain[..bodyLength], aad);
    }

    private void Bind(long index, bool last, Span<byte> nonce, Span<byte> aad)
    {
        _prefix.CopyTo(nonce);
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], index);
        _fileId.CopyTo(aad);
        BinaryPrimitives.WriteInt32BigEndian(aad.Slice(16, 4), _generation);
        BinaryPrimitives.WriteInt64BigEndian(aad.Slice(20, 8), index);
        aad[28] = last ? (byte)1 : (byte)0;
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>Sizes, offsets and whole-stream decryption for <c>PNE1</c> files.</summary>
public static class SealedFile
{
    /// <summary>Whether this computer can encrypt at all; if not, the app says so instead of syncing unencrypted.</summary>
    public static bool IsSupported => AesGcm.IsSupported;

    public static long SegmentCount(long plainLength) =>
        plainLength == 0 ? 1 : (plainLength + SealedSegments.SegmentSize - 1) / SealedSegments.SegmentSize;

    /// <summary>The encrypted size of a file of <paramref name="plainLength"/> bytes (≈ 89 bytes + 16 per MiB more).</summary>
    public static long EncryptedLength(long plainLength) =>
        SealedHeader.Size + SegmentCount(plainLength) * SealedSegments.TagSize + plainLength;

    /// <summary>The plain size of an encrypted file of <paramref name="encryptedLength"/> bytes, or -1 if no plain size fits.</summary>
    public static long PlainLength(long encryptedLength)
    {
        var body = encryptedLength - SealedHeader.Size;
        if (body < SealedSegments.TagSize)
            return -1;
        var full = body / SealedSegments.EncryptedSegmentSize;
        var rest = body % SealedSegments.EncryptedSegmentSize;
        if (rest == 0)
            return full * SealedSegments.SegmentSize;
        return rest < SealedSegments.TagSize ? -1 : full * SealedSegments.SegmentSize + rest - SealedSegments.TagSize;
    }

    /// <summary>Where segment <paramref name="index"/> starts in the encrypted file.</summary>
    public static long SegmentOffset(long index) => SealedHeader.Size + index * SealedSegments.EncryptedSegmentSize;

    /// <summary>
    /// The segment a resumed download continues from, given how many plain bytes are already safely written: always a
    /// whole segment, so the part file is cut back to <c>index × 1 MiB</c> and the request asks from
    /// <see cref="SegmentOffset"/>.
    /// </summary>
    public static long ResumeSegment(long plainBytesWritten) => plainBytesWritten / SealedSegments.SegmentSize;

    /// <summary>Decrypts a whole encrypted file (header first) into <paramref name="plain"/> and returns its header.</summary>
    public static async Task<SealedHeader> DecryptAsync(Stream encrypted, Stream plain, KeyRing ring, CancellationToken ct = default)
    {
        var headerBytes = new byte[SealedHeader.Size];
        if (await ReadFullAsync(encrypted, headerBytes, ct).ConfigureAwait(false) < SealedHeader.Size)
            throw new CryptographicException("An encrypted file was cut short.");
        var header = SealedHeader.Parse(headerBytes);
        await DecryptSegmentsAsync(header, ring, encrypted, firstSegment: 0, plain, ct).ConfigureAwait(false);
        return header;
    }

    /// <summary>
    /// Decrypts segments from <paramref name="firstSegment"/> to the end; <paramref name="encrypted"/> starts at
    /// <see cref="SegmentOffset"/>(<paramref name="firstSegment"/>). The last segment is the one followed by the end
    /// of the stream, so a file cut at a segment boundary fails (its new last segment wasn't sealed as last).
    /// </summary>
    public static async Task DecryptSegmentsAsync(SealedHeader header, KeyRing ring, Stream encrypted, long firstSegment,
        Stream plain, CancellationToken ct = default)
    {
        var dataKey = header.UnwrapDataKey(ring);
        using var segments = new SealedSegments(header, dataKey);
        CryptographicOperations.ZeroMemory(dataKey);
        var pool = ArrayPool<byte>.Shared;
        var current = pool.Rent(SealedSegments.EncryptedSegmentSize);
        var next = pool.Rent(SealedSegments.EncryptedSegmentSize);
        var output = pool.Rent(SealedSegments.SegmentSize);
        try
        {
            var index = firstSegment;
            var currentLength = await ReadFullAsync(encrypted, current.AsMemory(0, SealedSegments.EncryptedSegmentSize), ct).ConfigureAwait(false);
            while (true)
            {
                if (currentLength < SealedSegments.TagSize)
                    throw new CryptographicException("An encrypted file was cut short.");
                var nextLength = currentLength < SealedSegments.EncryptedSegmentSize
                    ? 0
                    : await ReadFullAsync(encrypted, next.AsMemory(0, SealedSegments.EncryptedSegmentSize), ct).ConfigureAwait(false);
                var last = nextLength == 0;
                segments.Decrypt(index, last, current.AsSpan(0, currentLength), output);
                await plain.WriteAsync(output.AsMemory(0, currentLength - SealedSegments.TagSize), ct).ConfigureAwait(false);
                if (last)
                    return;
                (current, next) = (next, current);
                currentLength = nextLength;
                index++;
            }
        }
        finally
        {
            pool.Return(current, clearArray: true);
            pool.Return(next, clearArray: true);
            pool.Return(output, clearArray: true);
        }
    }

    internal static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    internal static int ReadFull(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer[total..]);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }
}

/// <summary>
/// A read-only stream of the encrypted form of a plain stream: the header, then the sealed segments. It can start at
/// any encrypted offset (a resumed upload continues at the nest's offset): it seeks the plain stream to the segment
/// that holds the offset and drops the bytes before it, so the bytes are exactly the ones a fresh start would give.
/// If the plain stream turns out shorter or longer than <c>plainLength</c>, it throws (the file changed while it was
/// sent; the upload starts again).
/// </summary>
public sealed class SealedEncryptStream : Stream
{
    private readonly Stream _plain;
    private readonly bool _leaveOpen;
    private readonly long _plainLength;
    private readonly long _segmentCount;
    private readonly byte[] _header;
    private readonly SealedSegments _segments;
    private readonly byte[] _plainBuffer = new byte[SealedSegments.SegmentSize];
    private readonly byte[] _sealed = new byte[SealedSegments.EncryptedSegmentSize];
    private long _position;
    private long _nextSegment;
    private int _sealedOffset;
    private int _sealedLength;

    public SealedEncryptStream(Stream plain, long plainLength, SealedHeader header, ReadOnlySpan<byte> dataKey,
        long startOffset = 0, bool leaveOpen = false)
    {
        _plain = plain;
        _leaveOpen = leaveOpen;
        _plainLength = plainLength;
        _segmentCount = SealedFile.SegmentCount(plainLength);
        _header = header.ToBytes();
        _segments = new SealedSegments(header, dataKey);
        Length = SealedFile.EncryptedLength(plainLength);
        if (startOffset < 0 || startOffset > Length)
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        _position = startOffset;
        if (startOffset > SealedHeader.Size)
        {
            var index = (startOffset - SealedHeader.Size) / SealedSegments.EncryptedSegmentSize;
            if (index > 0)
            {
                if (!plain.CanSeek)
                    throw new NotSupportedException("A resumed upload needs a seekable file.");
                plain.Seek(index * SealedSegments.SegmentSize, SeekOrigin.Begin);
            }
            _nextSegment = index;
            if (index < _segmentCount)
            {
                SealNext();
                _sealedOffset = (int)(startOffset - SealedFile.SegmentOffset(index));
            }
        }
    }

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length && _position < Length)
        {
            if (_position < SealedHeader.Size)
            {
                var n = (int)Math.Min(buffer.Length - written, SealedHeader.Size - _position);
                _header.AsSpan((int)_position, n).CopyTo(buffer[written..]);
                written += n;
                _position += n;
                continue;
            }
            if (_sealedOffset >= _sealedLength)
                SealNext();
            var take = Math.Min(buffer.Length - written, _sealedLength - _sealedOffset);
            _sealed.AsSpan(_sealedOffset, take).CopyTo(buffer[written..]);
            _sealedOffset += take;
            written += take;
            _position += take;
        }
        return written;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void SealNext()
    {
        var index = _nextSegment++;
        var last = index == _segmentCount - 1;
        var want = (int)(last ? _plainLength - index * SealedSegments.SegmentSize : SealedSegments.SegmentSize);
        var got = SealedFile.ReadFull(_plain, _plainBuffer.AsSpan(0, want));
        if (got != want || (last && _plain.ReadByte() >= 0))
            throw new IOException("The file changed while it was being sent.");
        _segments.Encrypt(index, last, _plainBuffer.AsSpan(0, want), _sealed);
        _sealedLength = want + SealedSegments.TagSize;
        _sealedOffset = 0;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _segments.Dispose();
            CryptographicOperations.ZeroMemory(_plainBuffer);
            if (!_leaveOpen)
                _plain.Dispose();
        }
        base.Dispose(disposing);
    }
}
