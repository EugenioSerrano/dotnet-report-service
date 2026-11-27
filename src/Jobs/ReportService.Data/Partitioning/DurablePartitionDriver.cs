using System.Globalization;
using ReportService.Storage;

namespace ReportService.Data.Partitioning;

/// <summary>
/// What one durable partition pass produced: the ordered index page of its committed
/// blocks, as held in memory after the pass, and the write facts of the page the pass
/// stored beside them. The blocks and the index page are durable in the object store;
/// this is the caller's map of them. The write facts let the caller record the index in
/// its partition record without a second writer of the durable index.
/// </summary>
/// <param name="Index">The ordered index page of the partition's committed blocks.</param>
/// <param name="IndexObjectKey">The key the index page was written under.</param>
/// <param name="IndexSizeBytes">The size of the stored index page.</param>
/// <param name="IndexSha256">The SHA-256 of the stored index page, lower-case hexadecimal.</param>
public sealed record DurablePartition(
    BlockIndexPage Index,
    string IndexObjectKey,
    long IndexSizeBytes,
    string IndexSha256);

/// <summary>
/// Runs the <see cref="StreamingPartitioner"/> against an object store: it writes each
/// block object and the final index page through the storage port, and surfaces the
/// per-block commit checkpoint so the caller (the durable activity) can carry it in the
/// heartbeat details for a restart. There is no checkpoint datastore — the
/// committed block objects and the durable index entries, carried by the checkpoint, are
/// the committed-block record. On resume the driver is given the last
/// checkpoint and the committed blocks already in the store, re-reads the snapshot from
/// the start, and continues under a fresh attempt, rewriting only the blocks past the
/// checkpoint. It is the live PDF data step's partition pass and the restart-safe,
/// at-scale, real-storage entry point for the multi-block engine.
/// </summary>
public sealed class DurablePartitionDriver
{
    private readonly IObjectStore _store;
    private readonly PartitionSpec _spec;

    /// <summary>Builds a driver over <paramref name="store"/> applying <paramref name="spec"/>.</summary>
    /// <param name="store">The object store the blocks and index page are written to.</param>
    /// <param name="spec">The group key, amount field and limits the pass applies.</param>
    public DurablePartitionDriver(IObjectStore store, PartitionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(spec);
        _store = store;
        _spec = spec;
    }

    /// <summary>Partitions <paramref name="document"/> for <paramref name="jobId"/> against the store.</summary>
    /// <param name="jobId">The job the blocks live under; a valid lower-case key segment.</param>
    /// <param name="document">The values of the snapshot, header first.</param>
    /// <param name="resumeFrom">The checkpoint to resume from, or null for a clean pass.</param>
    /// <param name="cancellationToken">The pass's cancellation token.</param>
    /// <param name="onBlockCommitted">Invoked with the durable checkpoint each time a block is committed.</param>
    /// <returns>The committed partition's ordered index page.</returns>
    public async Task<DurablePartition> RunAsync(
        string jobId,
        IAsyncEnumerable<RawRecord> document,
        PartitionCheckpoint? resumeFrom,
        CancellationToken cancellationToken,
        Action<PartitionCheckpoint>? onBlockCommitted = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(document);

        var attempt = Guid.NewGuid().ToString("N");
        var sink = new ObjectStoreBlockSink(_store, jobId, attempt, cancellationToken);
        var partition = await StreamingPartitioner
            .PartitionAsync(document, sink, _spec, cancellationToken, onBlockCommitted, resumeFrom)
            .ConfigureAwait(false);

        // The final durable index page, written once under this attempt's key.
        var indexKey = ObjectKey.FromSegments("index", jobId, attempt);
        var bytes = BlockIndexPage.Serialize(partition.Index);
        using var content = new MemoryStream(bytes, writable: false);
        var write = await _store.WriteAsync(indexKey, content, cancellationToken).ConfigureAwait(false);
        if (!write.Succeeded || write.Value is null)
        {
            throw new InvalidOperationException("the durable partition index page could not be written");
        }

        return new DurablePartition(
            partition.Index,
            indexKey.ToString(),
            write.Value.Size,
            write.Value.Sha256);
    }

    // A sink that writes each block document to the object store under an attempt-scoped key.
    private sealed class ObjectStoreBlockSink(IObjectStore store, string jobId, string attempt, CancellationToken cancellationToken)
        : IBlockSink
    {
        public Stream OpenBlock(int blockIndex, out string key)
        {
            var objectKey = ObjectKey.FromSegments("block", jobId, blockIndex.ToString(CultureInfo.InvariantCulture), attempt);
            key = objectKey.ToString();
            return new StoreBlockStream(store, objectKey, cancellationToken);
        }
    }

    // A write-only stream that buffers one block document and writes it to the store, once,
    // when it is disposed — the point at which the partitioner treats the block as durable.
    private sealed class StoreBlockStream(IObjectStore store, ObjectKey key, CancellationToken cancellationToken) : Stream
    {
        private readonly MemoryStream _buffer = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _buffer.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Write(buffer);

        public override async ValueTask DisposeAsync()
        {
            _buffer.Position = 0;
            var write = await store.WriteAsync(key, _buffer, cancellationToken).ConfigureAwait(false);
            await _buffer.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            if (!write.Succeeded)
            {
                throw new InvalidOperationException("a partition block could not be written to durable storage");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
