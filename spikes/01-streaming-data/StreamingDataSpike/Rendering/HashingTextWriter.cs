using System.Security.Cryptography;
using System.Text;

namespace StreamingDataSpike.Rendering;

/// <summary>
/// A text sink that keeps nothing: it encodes what is written as UTF-8
/// (no byte-order mark) and feeds it to an incremental SHA-256.
/// </summary>
internal sealed class HashingTextWriter : TextWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Encoder _encoder = Utf8.GetEncoder();
    private readonly byte[] _bytes = new byte[16_384];

    public override Encoding Encoding => Utf8;

    /// <summary>UTF-8 bytes hashed so far.</summary>
    public long Bytes { get; private set; }

    public override void Write(char value) => Write(new ReadOnlySpan<char>(in value));

    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    public override void Write(string? value)
    {
        if (value is not null)
        {
            Write(value.AsSpan());
        }
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        while (!buffer.IsEmpty)
        {
            _encoder.Convert(buffer, _bytes, flush: false, out var used, out var produced, out _);
            Append(produced);
            buffer = buffer[used..];
        }
    }

    // The asynchronous members complete synchronously: the base class would
    // queue every write to the thread pool, and one engine writes only
    // through WriteAsync, which would measure the queue instead of the engine.
    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(char[] buffer, int index, int count)
    {
        Write(buffer, index, count);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return Task.CompletedTask;
    }

    public override Task FlushAsync() => Task.CompletedTask;

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Flushes the encoder and returns the lower-case hexadecimal SHA-256.</summary>
    public string Finish()
    {
        _encoder.Convert(ReadOnlySpan<char>.Empty, _bytes, flush: true, out _, out var produced, out _);
        Append(produced);
        return Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Append(int produced)
    {
        _hash.AppendData(_bytes, 0, produced);
        Bytes += produced;
    }
}
