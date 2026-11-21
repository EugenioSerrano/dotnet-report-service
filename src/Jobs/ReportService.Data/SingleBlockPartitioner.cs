namespace ReportService.Data;

/// <summary>
/// The single-block partitioner: one sequential pass over the reader that admits the
/// records into exactly one block and writes the block document to a sink. It holds
/// the header and the open block, never the record set. The first record that would
/// take the block past its effective limit stops the pass with a quota refusal, so data
/// that cannot be printed as one block fails before anything is written for it.
/// </summary>
public static class SingleBlockPartitioner
{
    /// <summary>The version of these partitioning rules, recorded with every partition.</summary>
    public const int PartitionerVersion = 1;

    /// <summary>Admits the records of <paramref name="document"/> into one block and writes it.</summary>
    /// <param name="document">The values of one snapshot, header first.</param>
    /// <param name="sink">The stream the block document is written to.</param>
    /// <param name="cancellationToken">The pass's cancellation token.</param>
    /// <param name="progress">Reports the records admitted so far.</param>
    /// <param name="limit">The limit the pass applies; the block's hard maximum when none is given.</param>
    /// <returns>What the block holds.</returns>
    public static async Task<PartitionSummary> PartitionAsync(
        IAsyncEnumerable<RawRecord> document,
        Stream sink,
        CancellationToken cancellationToken = default,
        Action<long>? progress = null,
        SingleBlockLimit? limit = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sink);
        var bounds = limit ?? SingleBlockLimit.Unconfigured;

        await using var values = document.GetAsyncEnumerator(cancellationToken);
        if (!await values.MoveNextAsync().ConfigureAwait(false) || values.Current.Ordinal != 0)
        {
            throw new DataContractViolation(DataContractViolation.DataInvalid, "profile-violation", null, 0, 0);
        }

        BlockDocumentWriter.WriteStart(sink, values.Current.Json.Span);

        long recordCount = 0;
        long recordBytes = 0;
        var first = true;
        while (await values.MoveNextAsync().ConfigureAwait(false))
        {
            var record = values.Current;
            if (!bounds.TryAdmit(recordCount, recordBytes, record.ByteLength, out var refusal))
            {
                // The record's own position is what the diagnostic carries: the limit's
                // answer names the dimension and the applied value, never a value.
                refusal = refusal with { Ordinal = record.Ordinal, Offset = record.Offset };
                throw new DataContractViolation(
                    DataContractViolation.QuotaExceeded,
                    refusal.Reason,
                    "block",
                    refusal.Ordinal,
                    record.Offset);
            }

            BlockDocumentWriter.WriteRecord(sink, record.Json.Span, first);
            first = false;
            recordCount++;
            recordBytes += record.ByteLength;
            progress?.Invoke(recordCount);
        }

        BlockDocumentWriter.WriteEnd(sink);
        return new PartitionSummary(1, recordCount, recordBytes, 1);
    }
}
