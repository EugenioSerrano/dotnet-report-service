using System.Text;

namespace ReportService.Storage.Tests;

/// <summary>The hashing writer over the test's own memory sink.</summary>
public sealed class HashingWriteStreamTests
{
    // SHA-256 of the ASCII bytes "abc" (FIPS 180-2 test vector).
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompletedStreamReportsTheExactHashAndSizeOfAKnownSequence()
    {
        using var sink = new MemoryStream();
        await using var hashing = new HashingWriteStream(sink, leaveOpen: true);

        await hashing.WriteAsync(Encoding.ASCII.GetBytes("ab"), Cancellation);
        hashing.Write(Encoding.ASCII.GetBytes("c"), 0, 1);
        await hashing.CompleteAsync(Cancellation);

        Assert.True(hashing.IsCompleted);
        Assert.Equal(3, hashing.Size);
        Assert.Equal(AbcSha256, hashing.Sha256);
        Assert.Equal("abc", Encoding.ASCII.GetString(sink.ToArray()));
    }

    [Fact]
    public async Task EveryWriteOverloadIsCountedAndHashed()
    {
        using var sink = new MemoryStream();
        await using var hashing = new HashingWriteStream(sink, leaveOpen: true);

        hashing.Write("a"u8);
        await hashing.WriteAsync(Encoding.ASCII.GetBytes("xbx"), 1, 1, Cancellation);
        await hashing.WriteAsync("c"u8.ToArray().AsMemory(), Cancellation);
        hashing.Flush();
        await hashing.FlushAsync(Cancellation);
        await hashing.CompleteAsync(Cancellation);

        Assert.Equal(AbcSha256, hashing.Sha256);
    }

    [Fact]
    public async Task FiguresAreUnavailableUntilCompleted()
    {
        using var sink = new MemoryStream();
        await using var hashing = new HashingWriteStream(sink);
        await hashing.WriteAsync("abc"u8.ToArray(), Cancellation);

        Assert.False(hashing.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => hashing.Size);
        Assert.Throws<InvalidOperationException>(() => hashing.Sha256);
    }

    [Fact]
    public async Task CompletedStreamRefusesFurtherWrites()
    {
        using var sink = new MemoryStream();
        await using var hashing = new HashingWriteStream(sink, leaveOpen: true);
        await hashing.CompleteAsync(Cancellation);

        Assert.False(hashing.CanWrite);
        Assert.Throws<InvalidOperationException>(() => hashing.Write("x"u8));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hashing.CompleteAsync(Cancellation));
    }

    [Fact]
    public async Task SinkThatFailsMidwayLeavesNoCommit()
    {
        await using var hashing = new HashingWriteStream(new FailingSink(failAfter: 2));

        hashing.Write("ab"u8);
        Assert.Throws<IOException>(() => hashing.Write("c"u8));
        await Assert.ThrowsAsync<InvalidOperationException>(() => hashing.CompleteAsync(Cancellation));

        Assert.False(hashing.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => hashing.Sha256);
    }

    [Fact]
    public async Task SinkThatFailsAsynchronouslyOrOnFlushLeavesNoCommit()
    {
        await using var onWrite = new HashingWriteStream(new FailingSink(failAfter: 0));
        await Assert.ThrowsAsync<IOException>(() => onWrite.WriteAsync("a"u8.ToArray(), Cancellation).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => onWrite.WriteAsync("a"u8.ToArray(), Cancellation).AsTask());

        await using var onFlush = new HashingWriteStream(new FailingSink(failAfter: 100, failOnFlush: true));
        await onFlush.WriteAsync("a"u8.ToArray(), Cancellation);
        await Assert.ThrowsAsync<IOException>(() => onFlush.CompleteAsync(Cancellation));
        Assert.False(onFlush.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => onFlush.CompleteAsync(Cancellation));
    }

    [Fact]
    public async Task DisposingClosesTheSinkUnlessItIsLeftOpen()
    {
        var closed = new MemoryStream();
        var open = new MemoryStream();
        var closedAsync = new MemoryStream();

        new HashingWriteStream(closed).Dispose();
        new HashingWriteStream(open, leaveOpen: true).Dispose();
        await new HashingWriteStream(closedAsync).DisposeAsync();
        var disposed = new HashingWriteStream(new MemoryStream());
        disposed.Dispose();
        disposed.Dispose();

        Assert.False(closed.CanWrite);
        Assert.True(open.CanWrite);
        Assert.False(closedAsync.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => disposed.Write("a"u8));
        open.Dispose();
    }

    [Fact]
    public void StreamIsWriteOnlyAndForwardOnly()
    {
        using var hashing = new HashingWriteStream(new MemoryStream());

        Assert.False(hashing.CanRead);
        Assert.False(hashing.CanSeek);
        Assert.True(hashing.CanWrite);
        Assert.Throws<NotSupportedException>(() => hashing.Length);
        Assert.Throws<NotSupportedException>(() => hashing.Position);
        Assert.Throws<NotSupportedException>(() => hashing.Position = 0);
        Assert.Throws<NotSupportedException>(() => hashing.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => hashing.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => hashing.SetLength(1));
    }

    [Fact]
    public void SinkMustBeWritable()
    {
        using var readOnly = new MemoryStream([], writable: false);

        Assert.Throws<ArgumentNullException>(() => new HashingWriteStream(null!));
        Assert.Throws<ArgumentException>(() => new HashingWriteStream(readOnly));
    }

    private sealed class FailingSink(int failAfter, bool failOnFlush = false) : Stream
    {
        private int _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position { get; set; }

        public override void Flush()
        {
            if (failOnFlush)
            {
                throw new IOException("The sink failed on purpose.");
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flush();
            return Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_written + buffer.Length > failAfter)
            {
                throw new IOException("The sink failed on purpose.");
            }

            _written += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
