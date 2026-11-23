using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using ReportService.Api.Artifacts;
using ReportService.Api.Telemetry;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;

namespace ReportService.Api.Tests.Artifacts;

/// <summary>
/// The verified copy on its own, without a server: the hold-back that keeps one chunk in
/// hand, the chunk boundaries at both ends, the transfer a corrupt object cuts before its
/// last chunk is written, a source longer than the row says, and a client that stops
/// accepting bytes — which is the client's doing and is never counted as a corruption.
/// The response is a stream of the case's own, so what the endpoint wrote is read byte by
/// byte, and the request's lifetime feature records whether the request was aborted.
/// </summary>
public sealed class ArtifactStreamingTests
{
    private const string ArtifactId = "artifact-0123456789abcdef0123456789abcdef";

    /// <summary>The meter this class's own instruments live on, so no case hears its counts.</summary>
    private readonly string _meterName = "reportservice-artifact-streaming-" + Guid.NewGuid().ToString("N");

    private readonly DownloadInstruments _instruments;

    private readonly LogCapture _logs = new();

    /// <summary>Starts the listener on this class's own meter, before any transfer is counted.</summary>
    public ArtifactStreamingTests() => _instruments = new DownloadInstruments(_meterName);

    [Fact]
    public async Task AnObjectOfOneChunkIsDeliveredWhole()
    {
        var content = Body(100);
        var (http, body, lifetime) = Context();

        var delivered = await CopyAsync(http, content, content.LongLength);

        Assert.True(delivered);
        Assert.Equal(content, body.ToArray());
        Assert.False(lifetime.Aborted);
        Assert.Equal(0, _instruments.IntegrityFailures);
    }

    [Fact]
    public async Task AnObjectOfAnExactMultipleOfTheBufferIsDeliveredWhole()
    {
        var content = Body(3 * ArtifactStreaming.BufferLength);
        var (http, body, lifetime) = Context();

        var delivered = await CopyAsync(http, content, content.LongLength);

        Assert.True(delivered);
        Assert.Equal(content.LongLength, body.Length);
        Assert.Equal(content, body.ToArray());
        Assert.False(lifetime.Aborted);
    }

    [Fact]
    public async Task ASameSizeCorruptionCutsTheTransferBeforeTheHeldChunkIsWritten()
    {
        var content = Body(2 * ArtifactStreaming.BufferLength);
        var stored = (byte[])content.Clone();
        stored[^1] ^= 0x5a;
        var (http, body, lifetime) = Context();

        var delivered = await CopyAsync(http, stored, content.LongLength, expectedHashOf: content);

        // Every chunk but the last one reached the client, and the last one never did.
        Assert.False(delivered);
        Assert.True(lifetime.Aborted, "a verification failure has to abort the response");
        Assert.Equal(content.LongLength - ArtifactStreaming.BufferLength, body.Length);
        Assert.Equal(1, _instruments.IntegrityFailures);
        Assert.Contains(
            _logs.Lines,
            line => line.StartsWith("Error:", StringComparison.Ordinal)
                && line.Contains(ArtifactId, StringComparison.Ordinal)
                && line.Contains("(hash)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnObjectLongerThanItsAnnouncedLengthIsCutBeforeTheExtraByteIsWritten()
    {
        var content = Body((2 * ArtifactStreaming.BufferLength) + 1);
        var (http, body, lifetime) = Context();

        var delivered = await CopyAsync(http, content, content.LongLength - 1);

        Assert.False(delivered);
        Assert.True(lifetime.Aborted);
        Assert.Equal(ArtifactStreaming.BufferLength, body.Length);
        Assert.Equal(1, _instruments.IntegrityFailures);
        Assert.Contains(
            _logs.Lines,
            line => line.StartsWith("Error:", StringComparison.Ordinal)
                && line.Contains("(longer)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClientThatStopsAcceptingBytesEndsTheDownloadWithoutCountingIt()
    {
        var content = Body(2 * ArtifactStreaming.BufferLength);
        var (http, _, lifetime) = Context();
        var refused = new RefusingBody(succeedingWrites: 1);
        http.Response.Body = refused;

        var delivered = await CopyAsync(http, content, content.LongLength);

        // The object was never at fault: nothing is counted, nothing is aborted, and the record
        // is the Debug one that says the client left.
        Assert.False(delivered);
        Assert.Equal(0, _instruments.IntegrityFailures);
        Assert.False(lifetime.Aborted);
        Assert.DoesNotContain(_logs.Lines, line => line.StartsWith("Error:", StringComparison.Ordinal));
        Assert.Contains(
            _logs.Lines,
            line => line.StartsWith("Debug:", StringComparison.Ordinal)
                && line.Contains("ended by the client", StringComparison.Ordinal));
    }

    private static (DefaultHttpContext Http, MemoryStream Body, RecordingLifetime Lifetime) Context()
    {
        var lifetime = new RecordingLifetime();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body, lifetime);
    }

    private async Task<bool> CopyAsync(
        DefaultHttpContext http,
        byte[] stored,
        long expectedSize,
        byte[]? expectedHashOf = null)
    {
        var expected = expectedHashOf ?? stored;
        using var inner = new MemoryStream(stored, writable: false);
        using var reader = new VerifyingReadStream(inner, expected.LongLength, HashOf(expected));
        using var factory = new LoggerFactory([_logs]);
        using var metrics = new ArtifactDownloadMetrics(MeterIdentity.ForHost(_meterName));
        var streaming = new ArtifactStreaming(metrics, factory.CreateLogger<ArtifactStreaming>());

        return await streaming.CopyVerifiedAsync(
            http,
            reader,
            expectedSize,
            ArtifactId,
            TestContext.Current.CancellationToken);
    }

    private static string HashOf(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static byte[] Body(int length)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index % 251);
        }

        return content;
    }

    /// <summary>The request's lifetime feature, recording whether the response was aborted.</summary>
    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        /// <summary>Whether the response was aborted.</summary>
        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }

    /// <summary>A response body that accepts a number of writes and then finds its client gone.</summary>
    private sealed class RefusingBody(int succeedingWrites) : Stream
    {
        private int _writes;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) > succeedingWrites)
            {
                throw new IOException("the client went away");
            }

            return ValueTask.CompletedTask;
        }
    }
}
