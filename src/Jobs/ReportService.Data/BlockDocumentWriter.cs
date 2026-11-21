namespace ReportService.Data;

/// <summary>
/// Writes the block document: the same profile a snapshot is written in, holding the
/// header the snapshot carried and the records the block took, with the bytes of each
/// value copied as they were read. Nothing here parses, rounds or reorders a value.
/// </summary>
public static class BlockDocumentWriter
{
    /// <summary>Writes the contract version and the header, opening the item list.</summary>
    /// <param name="sink">The stream the document is written to.</param>
    /// <param name="headerJson">The header value, exactly as the snapshot held it.</param>
    public static void WriteStart(Stream sink, ReadOnlySpan<byte> headerJson)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write("""{"contractVersion":"1.0","header":"""u8);
        sink.Write(headerJson);
        sink.Write(""","items":["""u8);
    }

    /// <summary>Writes one record, separated from the one before it.</summary>
    /// <param name="sink">The stream the document is written to.</param>
    /// <param name="recordJson">The record value, exactly as the snapshot held it.</param>
    /// <param name="first">Whether this is the block's first record.</param>
    public static void WriteRecord(Stream sink, ReadOnlySpan<byte> recordJson, bool first)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!first)
        {
            sink.Write(","u8);
        }

        sink.Write(recordJson);
    }

    /// <summary>Closes the item list and the document.</summary>
    /// <param name="sink">The stream the document is written to.</param>
    public static void WriteEnd(Stream sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write("]}"u8);
    }
}
