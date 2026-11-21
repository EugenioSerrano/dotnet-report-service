using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ReportService.Storage.Tests;

/// <summary>
/// A deterministic, non-seekable source of <c>length</c> bytes generated from
/// a seed (SplitMix64), never held in memory as a whole. It can fail after a
/// given number of bytes, the way a broken upload does.
/// </summary>
public sealed class GeneratedContent : Stream
{
    private readonly long _length;
    private readonly ulong _seed;
    private readonly long _failAfter;
    private long _position;

    /// <summary>Creates the source; it throws <see cref="IOException"/> once <paramref name="failAfter"/> bytes were read, when given.</summary>
    public GeneratedContent(long length, ulong seed, long failAfter = -1)
    {
        _length = length;
        _seed = seed;
        _failAfter = failAfter;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>The SHA-256 of the content of a source with these arguments, lower-case hexadecimal.</summary>
    public static string Sha256Of(long length, ulong seed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var source = new GeneratedContent(length, seed);
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (_failAfter >= 0 && _position >= _failAfter)
        {
            throw new IOException("The generated source failed on purpose.");
        }

        var count = (int)Math.Min(buffer.Length, _length - _position);
        if (_failAfter >= 0)
        {
            count = (int)Math.Min(count, _failAfter - _position);
        }

        Span<byte> block = stackalloc byte[8];
        for (var i = 0; i < count;)
        {
            var position = _position + i;
            BinaryPrimitives.WriteUInt64LittleEndian(block, Mix(_seed + (ulong)(position / 8)));
            var offset = (int)(position % 8);
            var take = Math.Min(8 - offset, count - i);
            block.Slice(offset, take).CopyTo(buffer[i..]);
            i += take;
        }

        _position += count;
        return count;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private static ulong Mix(ulong value)
    {
        var z = value + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
