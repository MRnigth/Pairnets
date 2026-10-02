using System.Security.Cryptography;

namespace Tether.Core.Hashing;

/// <summary>
/// Pass-through stream that SHA-256 hashes every byte read from (or written to) the inner stream.
/// Used to hash uploads and downloads in flight, so content is never read twice.
/// </summary>
public sealed class HashingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Action<long>? _onProgress;
    private string? _final;

    public HashingStream(Stream inner, bool leaveOpen = false, Action<long>? onProgress = null)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
        _onProgress = onProgress;
    }

    /// <summary>Number of bytes that passed through the stream.</summary>
    public long BytesTransferred { get; private set; }

    /// <summary>Finalizes and returns the hash of everything that passed through.</summary>
    public string GetHash()
    {
        _final ??= ContentHash.ToHex(_hash.GetHashAndReset());
        return _final;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        if (_final is not null)
            throw new InvalidOperationException("Hash already finalized.");
        _hash.AppendData(data);
        BytesTransferred += data.Length;
        _onProgress?.Invoke(BytesTransferred);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = _inner.Read(buffer);
        Append(buffer[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Append(buffer.Span[..n]);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Append(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Append(buffer.Span);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            if (!_leaveOpen)
                _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        if (!_leaveOpen)
            await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
