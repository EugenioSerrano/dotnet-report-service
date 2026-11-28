namespace ReportService.Data;

/// <summary>
/// The limits the document profile fixes for one snapshot. They bound memory, not
/// usage: each one is the largest value the reader or the partitioner admits, and a
/// document that needs more fails closed with the code and the limit named here.
/// </summary>
public static class DataContractLimits
{
    /// <summary>The most bytes the header value may occupy.</summary>
    public const long HeaderBytes = 1_048_576;

    /// <summary>The most bytes one record may occupy. A record above <see cref="BlockRecordBytes"/> is the only record of its block.</summary>
    public const long RecordBytes = 2_097_152;

    /// <summary>The most bytes any single token may occupy, whitespace included; equal to the record bound, so a value the record bound admits is never refused as a token. Inside the header the header bound applies first.</summary>
    public const long TokenBytes = 2_097_152;

    /// <summary>The most levels of nesting a record or the header may reach.</summary>
    public const int Depth = 64;

    /// <summary>The most records one block may hold.</summary>
    public const int BlockRecords = 2_000;

    /// <summary>The most bytes of record content one block accumulates; a single record above it, up to <see cref="RecordBytes"/>, is placed in a block alone.</summary>
    public const long BlockRecordBytes = 1_048_576;

    /// <summary>The buffer the reader starts with, before it grows by doubling.</summary>
    public const int InitialBufferBytes = 4 * 1024;

    /// <summary>The largest buffer the reader ever uses; the cap of a single token.</summary>
    public const int BufferCapBytes = (int)TokenBytes;
}
