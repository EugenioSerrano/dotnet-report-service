namespace ReportService.Storage.Tests;

/// <summary>The verifying reader over the test's own memory source.</summary>
public sealed class VerifyingReadStreamTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MatchingContentIsReadWholeAndVerifiedAtItsEnd()
    {
        await using var reader = new VerifyingReadStream(new MemoryStream("abc"u8.ToArray()), 3, AbcSha256);
        var buffer = new byte[2];

        Assert.Equal(2, await reader.ReadAsync(buffer, Cancellation));
        Assert.False(reader.IsVerified);
        Assert.Equal(1, await reader.ReadAsync(buffer, Cancellation));
        Assert.Equal(0, await reader.ReadAsync(buffer, Cancellation));
        Assert.True(reader.IsVerified);
        Assert.Equal(0, await reader.ReadAsync(buffer, Cancellation));
    }

    [Fact]
    public void SynchronousReadsVerifyToo()
    {
        using var reader = new VerifyingReadStream(new MemoryStream("abc"u8.ToArray()), 3, AbcSha256);
        var buffer = new byte[8];

        Assert.Equal(3, reader.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, reader.Read(buffer.AsSpan()));
        Assert.True(reader.IsVerified);
        Assert.Equal(0, reader.Read(buffer.AsSpan()));
    }

    [Fact]
    public async Task EmptyBufferReadIsNotTheEndOfTheContent()
    {
        await using var reader = new VerifyingReadStream(new MemoryStream("abc"u8.ToArray()), 3, AbcSha256);

        Assert.Equal(0, await reader.ReadAsync(Memory<byte>.Empty, Cancellation));
        Assert.False(reader.IsVerified);
        await reader.CopyToAsync(Stream.Null, Cancellation);
        Assert.True(reader.IsVerified);
    }

    [Theory]
    [InlineData("abd", 3, "storage.integrity.hash-mismatch")]
    [InlineData("ab", 2, "storage.integrity.size-mismatch")]
    [InlineData("abcd", 4, "storage.integrity.size-mismatch")]
    public async Task EachKindOfMismatchIsAnIntegrityFailure(string content, long actualSize, string code)
    {
        await using var reader = new VerifyingReadStream(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content)), 3, AbcSha256);

        var failure = await Assert.ThrowsAsync<StorageIntegrityException>(() => reader.CopyToAsync(Stream.Null, Cancellation));

        Assert.Equal(StorageOutcome.IntegrityFailure, failure.Outcome);
        Assert.Equal(code, failure.Error.Code);
        Assert.Equal(3, failure.ExpectedSize);
        Assert.Equal(actualSize, failure.ActualSize);
        Assert.Equal(AbcSha256, failure.ExpectedSha256);
        Assert.NotEqual(AbcSha256, failure.ActualSha256);
        Assert.Same(failure, Assert.Throws<StorageIntegrityException>(() => reader.Read(new byte[1], 0, 1)));
    }

    [Fact]
    public void SynchronousReadDetectsAMismatch()
    {
        using var reader = new VerifyingReadStream(new MemoryStream("ab"u8.ToArray()), 3, AbcSha256);
        var buffer = new byte[8];

        Assert.Equal(2, reader.Read(buffer.AsSpan()));
        var failure = Assert.Throws<StorageIntegrityException>(() => reader.Read(buffer.AsSpan()));

        Assert.Equal("The object has 2 bytes; 3 were expected.", failure.Message);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using var source = new MemoryStream();
        using var unreadable = new UnreadableStream();

        Assert.Throws<ArgumentNullException>(() => new VerifyingReadStream(null!, 0, AbcSha256));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifyingReadStream(source, -1, AbcSha256));
        Assert.Throws<ArgumentException>(() => new VerifyingReadStream(source, 0, AbcSha256.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => new VerifyingReadStream(unreadable, 0, AbcSha256));
    }

    [Fact]
    public async Task StreamIsReadOnlyForwardOnlyAndDisposesItsSource()
    {
        var source = new MemoryStream("abc"u8.ToArray());
        var reader = new VerifyingReadStream(source, 3, AbcSha256);

        Assert.True(reader.CanRead);
        Assert.False(reader.CanSeek);
        Assert.False(reader.CanWrite);
        Assert.Throws<NotSupportedException>(() => reader.Length);
        Assert.Throws<NotSupportedException>(() => reader.Position);
        Assert.Throws<NotSupportedException>(() => reader.Position = 1);
        Assert.Throws<NotSupportedException>(() => reader.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => reader.SetLength(0));
        Assert.Throws<NotSupportedException>(() => reader.Write([1], 0, 1));
        reader.Flush();

        await reader.DisposeAsync();
        await reader.DisposeAsync();

        Assert.False(reader.CanRead);
        Assert.False(source.CanRead);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(new byte[1], 0, 1));
    }

    [Fact]
    public async Task VerifiedReadExposesTheExpectedFiguresAndReleasesTheObject()
    {
        var source = new MemoryStream("abc"u8.ToArray());
        var read = new VerifiedRead(source, 3, AbcSha256);

        Assert.Equal(3, read.Size);
        Assert.Equal(AbcSha256, read.Sha256);
        Assert.IsType<VerifyingReadStream>(read.Content);
        await read.Content.CopyToAsync(Stream.Null, Cancellation);
        read.Dispose();
        Assert.False(source.CanRead);

        var other = new MemoryStream();
        await new VerifiedRead(other, 0, AbcSha256).DisposeAsync();
        Assert.False(other.CanRead);
    }

    [Fact]
    public async Task ReadAsyncWithAnArrayIsVerified()
    {
        await using var reader = new VerifyingReadStream(new MemoryStream("abc"u8.ToArray()), 3, AbcSha256);
        var buffer = new byte[4];

        Assert.Equal(3, await reader.ReadAsync(buffer, 0, 4, Cancellation));
        Assert.Equal(0, await reader.ReadAsync(buffer, 0, 4, Cancellation));
        Assert.True(reader.IsVerified);
    }

    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
