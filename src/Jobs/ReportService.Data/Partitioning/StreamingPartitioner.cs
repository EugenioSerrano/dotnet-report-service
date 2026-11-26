using System.Security.Cryptography;
using System.Text.Json;

namespace ReportService.Data.Partitioning;

/// <summary>
/// The multi-block streaming partitioner: one sequential pass over an already-read record
/// stream that cuts it into contiguous, ordered blocks within the governed soft and hard
/// limits, prefers group boundaries once the soft target is reached, carries
/// continuity metadata across a group that spans a cut, lays the blocks out page-boundary,
/// records a durable ordered index, and computes each block's opening
/// carried-forward from previous records only. It holds only the header,
/// the current record and the open block, never the record set (flat memory), and is
/// deterministic for a fixed <see cref="PartitionerVersion"/>. A resume from a durable
/// checkpoint re-reads the stream from the start, skips the committed records without
/// rewriting them, and continues — yielding a block set and index identical to a clean
/// run. It does not apply the single-block refusal: that stays in the
/// single-block partitioner, which this type does not modify.
/// </summary>
public static class StreamingPartitioner
{
    /// <summary>The version of these partitioning rules, recorded with every partition.</summary>
    public const int PartitionerVersion = 2;

    /// <summary>Cuts <paramref name="document"/> into ordered blocks, writing each to <paramref name="sink"/>.</summary>
    /// <param name="document">The values of one snapshot, header first, already validated by the reader.</param>
    /// <param name="sink">Where each block document is written.</param>
    /// <param name="spec">The group key, amount field and limits the pass applies.</param>
    /// <param name="cancellationToken">The pass's cancellation token.</param>
    /// <param name="onBlockCommitted">Invoked with the durable checkpoint each time a block is committed.</param>
    /// <param name="resumeFrom">The checkpoint to resume from, or null for a clean pass.</param>
    /// <returns>The partition's durable, ordered index page.</returns>
    public static async Task<StreamingPartition> PartitionAsync(
        IAsyncEnumerable<RawRecord> document,
        IBlockSink sink,
        PartitionSpec spec,
        CancellationToken cancellationToken = default,
        Action<PartitionCheckpoint>? onBlockCommitted = null,
        PartitionCheckpoint? resumeFrom = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(spec);

        var committed = new List<BlockIndexEntry>();
        var accumulator = new CarriedForwardAccumulator();
        var nextBlockIndex = 0;
        var skipThroughOrdinal = 0;
        var pendingContinuesPrevious = false;
        string? pendingOpeningGroupKey = null;

        if (resumeFrom is not null)
        {
            committed.AddRange(resumeFrom.CommittedBlocks);
            accumulator = new CarriedForwardAccumulator(resumeFrom.CarriedForward);
            nextBlockIndex = resumeFrom.LastCommittedBlockIndex + 1;
            skipThroughOrdinal = resumeFrom.LastRecordOrdinal;
            pendingContinuesPrevious = resumeFrom.NextContinuesGroup;
            pendingOpeningGroupKey = resumeFrom.NextOpeningGroupKey;
        }

        ReadOnlyMemory<byte> header = default;
        var headerSeen = false;
        OpenBlockState? open = null;

        await foreach (var value in document.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (value.Ordinal == 0)
            {
                header = value.Json;
                headerSeen = true;
                continue;
            }

            if (value.Ordinal <= skipThroughOrdinal)
            {
                // A record a committed block already holds: it is read past, never rewritten on resume.
                continue;
            }

            if (!headerSeen)
            {
                throw new DataContractViolation(DataContractViolation.DataInvalid, "profile-violation", null, value.Ordinal, value.Offset);
            }

            var (amount, groupKey) = Inspect(spec, value);

            if (open is null)
            {
                open = OpenNewBlock(sink, header.Span, nextBlockIndex++, value.Ordinal, pendingContinuesPrevious, pendingContinuesPrevious ? pendingOpeningGroupKey : null, accumulator.OpeningCarriedForward);
            }
            else
            {
                var fill = spec.Limits.Classify(open.RecordCount, open.RecordBytes, value.ByteLength);
                var boundary = groupKey is { } next && open.LastGroupKey is { } last && !next.SameGroup(last);
                var spanning = fill == BlockFill.MustCut && groupKey is { } nextHard && open.LastGroupKey is { } lastHard && nextHard.SameGroup(lastHard);
                if (fill == BlockFill.MustCut || (fill == BlockFill.SoftReached && boundary))
                {
                    await CommitBlockAsync(open, spanning, accumulator, committed, onBlockCommitted).ConfigureAwait(false);
                    open = OpenNewBlock(sink, header.Span, nextBlockIndex++, value.Ordinal, spanning, spanning ? groupKey!.Value.ToText() : null, accumulator.OpeningCarriedForward);
                }
            }

            BlockDocumentWriter.WriteRecord(open.Stream, value.Json.Span, open.RecordCount == 0);
            open.RecordCount++;
            open.RecordBytes += value.ByteLength;
            accumulator.Add(amount);
            open.LastGroupKey = groupKey;
        }

        if (!headerSeen)
        {
            throw new DataContractViolation(DataContractViolation.DataInvalid, "truncated", null, 0, 0);
        }

        if (open is null && resumeFrom is null)
        {
            // The empty snapshot: exactly one empty block 0.
            open = OpenNewBlock(sink, header.Span, nextBlockIndex, firstOrdinal: 1, continuesPrevious: false, openingGroupKey: null, openingCarriedForward: accumulator.OpeningCarriedForward);
        }

        if (open is not null)
        {
            await CommitBlockAsync(open, continuesNext: false, accumulator, committed, onBlockCommitted).ConfigureAwait(false);
        }

        return new StreamingPartition(new BlockIndexPage(PartitionerVersion, committed, BlockIndexPage.PageBoundaryLayout));
    }

    private static OpenBlockState OpenNewBlock(
        IBlockSink sink,
        ReadOnlySpan<byte> header,
        int index,
        int firstOrdinal,
        bool continuesPrevious,
        string? openingGroupKey,
        decimal openingCarriedForward)
    {
        var inner = sink.OpenBlock(index, out var key);
        var stream = new HashingStream(inner);
        BlockDocumentWriter.WriteStart(stream, header);
        return new OpenBlockState
        {
            Index = index,
            Key = key,
            Stream = stream,
            FirstOrdinal = firstOrdinal,
            ContinuesPrevious = continuesPrevious,
            OpeningGroupKey = openingGroupKey,
            OpeningCarriedForward = openingCarriedForward,
        };
    }

    private static async Task CommitBlockAsync(
        OpenBlockState open,
        bool continuesNext,
        CarriedForwardAccumulator accumulator,
        List<BlockIndexEntry> committed,
        Action<PartitionCheckpoint>? onBlockCommitted)
    {
        BlockDocumentWriter.WriteEnd(open.Stream);
        var sha = open.Stream.Digest();
        var size = open.Stream.Count;

        // The block's own bytes are made durable before it counts as committed:
        // for a durable sink, disposing the stream is the store write.
        await open.Stream.DisposeAsync().ConfigureAwait(false);

        var blockTotal = accumulator.BlockTotal;
        committed.Add(new BlockIndexEntry(
            open.Index,
            open.Key,
            sha,
            size,
            open.FirstOrdinal,
            open.RecordCount,
            open.RecordBytes,
            open.ContinuesPrevious,
            continuesNext,
            open.OpeningGroupKey,
            open.OpeningCarriedForward,
            blockTotal));
        accumulator.CloseBlock();

        var lastOrdinal = open.RecordCount > 0 ? open.FirstOrdinal + (int)open.RecordCount - 1 : open.FirstOrdinal - 1;
        onBlockCommitted?.Invoke(new PartitionCheckpoint(
            open.Index,
            lastOrdinal,
            accumulator.OpeningCarriedForward,
            continuesNext,
            continuesNext ? open.LastGroupKey?.ToText() : null,
            [.. committed]));
    }

    // The amount and the group key of one record, read in a single parse. An amount with no
    // exact decimal where one is required fails DATA_INVALID reason=number-out-of-range;
    // a group key is a string or an integer, compared by type and value.
    private static (decimal Amount, GroupKey? Group) Inspect(PartitionSpec spec, RawRecord value)
    {
        using var document = JsonDocument.Parse(value.Json);
        var root = document.RootElement;
        var amount = 0m;
        if (spec.AmountField is not null
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(spec.AmountField, out var amountElement)
            && amountElement.ValueKind == JsonValueKind.Number
            && !amountElement.TryGetDecimal(out amount))
        {
            throw new DataContractViolation(DataContractViolation.DataInvalid, "number-out-of-range", null, value.Ordinal, value.Offset);
        }

        GroupKey? group = null;
        if (spec.GroupKeyField is not null
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(spec.GroupKeyField, out var groupElement))
        {
            group = groupElement.ValueKind switch
            {
                JsonValueKind.String => GroupKey.Text(groupElement.GetString()!),
                JsonValueKind.Number when groupElement.TryGetInt64(out var integer) => GroupKey.Integer(integer),
                _ => null,
            };
        }

        return (amount, group);
    }

    // The open block: the writing stream, its key and the running counts the index entry needs.
    private sealed class OpenBlockState
    {
        public required int Index { get; init; }

        public required string Key { get; init; }

        public required HashingStream Stream { get; init; }

        public required int FirstOrdinal { get; init; }

        public required bool ContinuesPrevious { get; init; }

        public required string? OpeningGroupKey { get; init; }

        public required decimal OpeningCarriedForward { get; init; }

        public long RecordCount { get; set; }

        public long RecordBytes { get; set; }

        public GroupKey? LastGroupKey { get; set; }
    }

    // A write-only stream that forwards to the block sink while computing the block document's
    // size and SHA-256 from exactly the bytes written, so the index entry's digest matches the
    // stored object without a second pass. Disposing it disposes the inner stream, which is how
    // a durable sink is told the block is complete.
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _count;

        public long Count => _count;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _count;

        public override long Position
        {
            get => _count;
            set => throw new NotSupportedException();
        }

        public string Digest() => Convert.ToHexStringLower(_hash.GetCurrentHash());

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            _hash.AppendData(buffer, offset, count);
            _count += count;
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _hash.AppendData(buffer);
            _count += buffer.Length;
            _inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _hash.Dispose();
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
