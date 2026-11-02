namespace ReportService.Data;

/// <summary>
/// The single-block partitioner: one sequential pass over the reader that admits the
/// records into exactly one block and writes the block document to a sink. It holds
/// the header and the open block, never the record set. The first record that would
/// take the block past its hard maximum stops the pass with a quota refusal, so data
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
    /// <returns>What the block holds.</returns>
    public static async Task<PartitionSummary> PartitionAsync(
        IAsyncEnumerable<RawRecord> document,
        Stream sink,
        CancellationToken cancellationToken = default,
        Action<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sink);

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
            var ordinal = checked((int)recordCount + 1);
            if (recordCount + 1 > DataContractLimits.BlockRecords
                || recordBytes + record.ByteLength > DataContractLimits.BlockRecordBytes)
            {
                throw new DataContractViolation(
                    DataContractViolation.QuotaExceeded,
                    null,
                    "block",
                    ordinal,
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
