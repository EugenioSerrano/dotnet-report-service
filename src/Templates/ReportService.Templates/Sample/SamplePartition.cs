using ReportService.Data;

namespace ReportService.Templates;

/// <summary>
/// A package's <c>sample.json</c> read as what the composer and the verifier need:
/// the header and the records exactly as <see cref="RenderContextFactory"/> reads
/// them, as one block - index 0, the first record at ordinal 1, first and last -
/// because the sample is one block by construction.
/// <para>
/// A sample above the hard maximum of a block is not a block any job's partitioner
/// would produce, so it is reported as <see cref="SampleRead.OverBound"/> rather
/// than rendered: at most 2 000 records and at most 1 MiB of record content, the
/// records' wire JSON summed as the partitioner sums it. A sample the delivered
/// reader rejects is <see cref="SampleRead.Unreadable"/>, never an exception. The
/// read allocates nothing but the values and the sample's own bytes; it holds no
/// engine and no package.
/// </para>
/// </summary>
/// <param name="Header">The header as the render context reads it.</param>
/// <param name="Records">The records, in ordinal order.</param>
public sealed record SamplePartition(
    IReadOnlyDictionary<string, object?> Header,
    IReadOnlyList<ComposedRecord> Records)
{
    /// <summary>The block the sample composes as: one block, records from ordinal one.</summary>
    public CompositionBlock Block => new(
        Index: 0,
        RecordCount: Records.Count,
        FirstRecordOrdinal: 1,
        IsFirst: true,
        IsLast: true);

    /// <summary>The same header with zero records: the empty sample a report must still print.</summary>
    public SamplePartition Empty() => this with { Records = [] };

    /// <summary>Reads <paramref name="sampleJson"/> as one block, or reports what stopped it.</summary>
    /// <param name="sampleJson">The bytes of the package's <c>sample.json</c>.</param>
    /// <param name="cancellationToken">The pass's cancellation token.</param>
    public static async Task<SampleRead> ReadAsync(
        ReadOnlyMemory<byte> sampleJson,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(sampleJson.ToArray(), writable: false);
        IReadOnlyDictionary<string, object?>? header = null;
        var records = new List<ComposedRecord>();
        long recordBytes = 0;
        try
        {
            await foreach (var value in HeaderItemsReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                if (value.Ordinal == 0)
                {
                    header = RenderContextFactory.Header(value.Json.Span);
                    continue;
                }

                recordBytes += value.ByteLength;
                records.Add(new ComposedRecord(value.Ordinal, RenderContextFactory.Item(value)));
            }
        }
        catch (DataContractViolation violation)
        {
            // The reader's own reason - a rule word - and never a value of the document.
            return new SampleRead.Unreadable(violation.Reason ?? violation.Code);
        }

        if (header is null)
        {
            return new SampleRead.Unreadable("header-missing");
        }

        if (records.Count > DataContractLimits.BlockRecords)
        {
            return new SampleRead.OverBound(SampleBound.Records, records.Count, recordBytes);
        }

        if (recordBytes > DataContractLimits.BlockRecordBytes)
        {
            return new SampleRead.OverBound(SampleBound.RecordBytes, records.Count, recordBytes);
        }

        return new SampleRead.Partitioned(new SamplePartition(header, records));
    }
}
