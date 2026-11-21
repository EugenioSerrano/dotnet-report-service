using System.Security.Cryptography;

namespace ReportService.Testing;

/// <summary>
/// A generated body that cannot be rewound and has no length: it produces a
/// deterministic pattern of a fixed size, block by block, and forgets each block as
/// it hands it on. A test that streams it proves the reader works on a source that
/// can only be read once, and it never holds the body in memory to do it.
/// </summary>
public sealed class GeneratedBodyStream : Stream
{
    private readonly long _length;
    private readonly byte _seed;
    private long _position;

    /// <summary>Creates a body of <paramref name="length"/> bytes.</summary>
    /// <param name="length">How many bytes the body carries.</param>
    /// <param name="seed">The value the pattern is derived from.</param>
    public GeneratedBodyStream(long length, byte seed = 0x5A)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
        _seed = seed;
    }

    /// <summary>The bytes handed on so far.</summary>
    public long BytesSent => _position;

    /// <summary>The SHA-256 of the whole body, computed from the same pattern without streaming it.</summary>
    public static string Sha256Of(long length, byte seed = 0x5A)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[64 * 1024];
        var remaining = length;
        var position = 0L;
        while (remaining > 0)
        {
            var take = (int)Math.Min(block.Length, remaining);
            Fill(block.AsSpan(0, take), position, seed);
            hash.AppendData(block, 0, take);
            position += take;
            remaining -= take;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("a generated body has no length.");

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("a generated body is forward only.");
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        var take = (int)Math.Min(buffer.Length, _length - _position);
        if (take <= 0)
        {
            return 0;
        }

        Fill(buffer[..take], _position, _seed);
        _position += take;
        return take;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("a generated body is forward only.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("a generated body is read only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("a generated body is read only.");

    // The pattern at a position: the seed mixed with the position, so a body of zeros is not special.
    private static void Fill(Span<byte> destination, long from, byte seed)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            var position = from + index;
            destination[index] = (byte)(seed ^ (position & 0xFF) ^ (position >> 8));
        }
    }
}
